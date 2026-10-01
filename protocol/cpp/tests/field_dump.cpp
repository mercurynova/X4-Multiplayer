#include "field_dump.h"

#include <cstdio>
#include <fstream>
#include <iterator>

namespace x4mp::test {
namespace {

using nlohmann::json;
using flatbuffers::ReadScalar;

std::string upper_camel(const std::string& snake) {
  std::string out;
  bool up = true;
  for (char c : snake) {
    if (c == '_') {
      up = true;
    } else if (up) {
      out.push_back(static_cast<char>(std::toupper(static_cast<unsigned char>(c))));
      up = false;
    } else {
      out.push_back(c);
    }
  }
  return out;
}

std::string hex(const std::uint8_t* data, std::size_t n) {
  static constexpr char kDigits[] = "0123456789abcdef";
  std::string s;
  s.reserve(n * 2);
  for (std::size_t i = 0; i < n; ++i) {
    s.push_back(kDigits[data[i] >> 4]);
    s.push_back(kDigits[data[i] & 0xF]);
  }
  return s;
}

// Reads a scalar of `type` stored at `p` (little-endian, unaligned).
json read_scalar(reflection::BaseType type, const std::uint8_t* p) {
  switch (type) {
    case reflection::Bool: return ReadScalar<std::uint8_t>(p) != 0;
    case reflection::Byte: return ReadScalar<std::int8_t>(p);
    case reflection::UByte:
    case reflection::UType: return ReadScalar<std::uint8_t>(p);
    case reflection::Short: return ReadScalar<std::int16_t>(p);
    case reflection::UShort: return ReadScalar<std::uint16_t>(p);
    case reflection::Int: return ReadScalar<std::int32_t>(p);
    case reflection::UInt: return ReadScalar<std::uint32_t>(p);
    case reflection::Long: return ReadScalar<std::int64_t>(p);
    case reflection::ULong: return ReadScalar<std::uint64_t>(p);
    case reflection::Float: return static_cast<double>(ReadScalar<float>(p));
    case reflection::Double: return ReadScalar<double>(p);
    default: throw std::runtime_error("not a scalar type");
  }
}

json default_scalar(const reflection::Field& f) {
  const auto type = f.type()->base_type();
  switch (type) {
    case reflection::Bool: return f.default_integer() != 0;
    case reflection::Float:
    case reflection::Double: return f.default_real();
    case reflection::ULong: return static_cast<std::uint64_t>(f.default_integer());
    default: return f.default_integer();
  }
}

bool is_scalar(reflection::BaseType t) {
  return t >= reflection::UType && t <= reflection::Double;
}

class Walker {
 public:
  explicit Walker(const reflection::Schema& schema) : schema_(schema) {}

  json table(const reflection::Object& obj, const flatbuffers::Table& t) const {
    json out = json::object();
    for (const auto* f : *obj.fields()) {
      const auto base = f->type()->base_type();
      if (base == reflection::UType || (base == reflection::Vector && f->type()->element() == reflection::UType))
        continue;  // folded into the union field below
      const std::string name = upper_camel(f->name()->str());
      if (is_scalar(base)) {
        const std::uint8_t* p = t.GetAddressOf(f->offset());
        out[name] = p ? read_scalar(base, p) : default_scalar(*f);
      } else if (base == reflection::String) {
        if (const auto* s = flatbuffers::GetFieldS(t, *f)) out[name] = s->str();
      } else if (base == reflection::Obj) {
        const auto& sub = *schema_.objects()->Get(f->type()->index());
        if (sub.is_struct()) {
          if (const auto* st = flatbuffers::GetFieldStruct(t, *f)) out[name] = structure(sub, *st);
        } else if (const auto* tt = flatbuffers::GetFieldT(t, *f)) {
          out[name] = table(sub, *tt);
        }
      } else if (base == reflection::Union) {
        const auto* tf = obj.fields()->LookupByKey((f->name()->str() + flatbuffers::UnionTypeFieldSuffix()).c_str());
        const auto type = flatbuffers::GetFieldI<std::uint8_t>(t, *tf);
        json u = json::object();
        u["type"] = type;
        if (type != 0) {
          const auto& member = flatbuffers::GetUnionType(schema_, obj, *f, t);
          const auto* body = flatbuffers::GetFieldT(t, *f);
          if (body == nullptr) throw std::runtime_error("union type set but value missing");
          u["value"] = table(member, *body);
        }
        out[name] = std::move(u);
      } else if (base == reflection::Vector) {
        if (const json v = vector(obj, t, *f); !v.is_null()) out[name] = v;
      } else {
        throw std::runtime_error("unsupported field type in " + obj.name()->str() + "." + f->name()->str());
      }
    }
    return out;
  }

  json structure(const reflection::Object& obj, const flatbuffers::Struct& st) const {
    json out = json::object();
    for (const auto* f : *obj.fields()) {
      const auto base = f->type()->base_type();
      const std::string name = upper_camel(f->name()->str());
      if (is_scalar(base)) {
        out[name] = read_scalar(base, st.GetAddressOf(f->offset()));
      } else if (base == reflection::Obj) {
        const auto& sub = *schema_.objects()->Get(f->type()->index());
        out[name] = structure(sub, *flatbuffers::GetFieldStruct(st, *f));
      } else {
        throw std::runtime_error("unsupported struct field in " + obj.name()->str());
      }
    }
    return out;
  }

 private:
  json vector(const reflection::Object& parent, const flatbuffers::Table& t, const reflection::Field& f) const {
    const auto* vec = flatbuffers::GetFieldAnyV(t, f);
    if (vec == nullptr) return nullptr;
    const auto elem = f.type()->element();
    const std::size_t n = vec->size();
    if (elem == reflection::UByte) return hex(vec->Data(), n);

    json out = json::array();
    if (is_scalar(elem)) {
      const std::size_t size = flatbuffers::GetTypeSize(elem);
      for (std::size_t i = 0; i < n; ++i) out.push_back(read_scalar(elem, vec->Data() + size * i));
    } else if (elem == reflection::String) {
      for (std::size_t i = 0; i < n; ++i)
        out.push_back(flatbuffers::GetAnyVectorElemPointer<const flatbuffers::String>(vec, i)->str());
    } else if (elem == reflection::Obj) {
      const auto& sub = *schema_.objects()->Get(f.type()->index());
      for (std::size_t i = 0; i < n; ++i) {
        if (sub.is_struct()) {
          out.push_back(structure(sub, *flatbuffers::GetAnyVectorElemAddressOf<const flatbuffers::Struct>(vec, i, sub.bytesize())));
        } else {
          out.push_back(table(sub, *flatbuffers::GetAnyVectorElemPointer<const flatbuffers::Table>(vec, i)));
        }
      }
    } else if (elem == reflection::Union) {
      // Vector of unions: a parallel [ubyte] "<name>_type" vector carries the member types.
      const auto* tf = parent.fields()->LookupByKey((f.name()->str() + flatbuffers::UnionTypeFieldSuffix()).c_str());
      const auto* types = flatbuffers::GetFieldAnyV(t, *tf);
      if (types == nullptr || types->size() != n) throw std::runtime_error("union vector type mismatch");
      const auto* enumdef = schema_.enums()->Get(f.type()->index());
      for (std::size_t i = 0; i < n; ++i) {
        const std::uint8_t type = types->Data()[i];
        json u = json::object();
        u["type"] = type;
        if (type != 0) {
          const auto* value = enumdef->values()->LookupByKey(type);
          const auto& member = *schema_.objects()->Get(value->union_type()->index());
          u["value"] = table(member, *flatbuffers::GetAnyVectorElemPointer<const flatbuffers::Table>(vec, i));
        }
        out.push_back(std::move(u));
      }
    } else {
      throw std::runtime_error("unsupported vector element type in " + parent.name()->str());
    }
    return out;
  }

  const reflection::Schema& schema_;
};

}  // namespace

Schema Schema::load(const std::string& path) {
  std::ifstream in(path, std::ios::binary);
  if (!in) throw std::runtime_error("cannot open " + path);
  Schema s;
  s.bytes_.assign(std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>());
  flatbuffers::Verifier v(s.bytes_.data(), s.bytes_.size());
  if (!reflection::VerifySchemaBuffer(v)) throw std::runtime_error("bad binary schema " + path);
  s.schema_ = reflection::GetSchema(s.bytes_.data());
  return s;
}

json dump_message(const reflection::Schema& schema, std::string_view table_name, std::span<const std::uint8_t> payload) {
  const std::string full = "X4MP.Proto." + std::string(table_name);
  const auto* obj = schema.objects()->LookupByKey(full.c_str());
  if (obj == nullptr) throw std::runtime_error("unknown table " + full);
  const auto* root = flatbuffers::GetAnyRoot(payload.data());
  return Walker(schema).table(*obj, *root);
}

}  // namespace x4mp::test
