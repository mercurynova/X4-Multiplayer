// T1.6: the client strategic-map hub, against real flatbuffers built the way the server builds them
// (MapModule.cs), so the reader is tested on the actual wire format and not on a hand-made struct.

#include <catch2/catch_test_macros.hpp>

#include <cstdint>
#include <string>
#include <vector>

#include "features/map/map_hub.h"
#include "message_ids_generated.h"
#include "session_generated.h"

namespace M = x4mp::features::map;
namespace P = X4MP::Proto;

namespace {

std::vector<std::uint8_t> finish(const flatbuffers::FlatBufferBuilder& fbb) {
  return std::vector<std::uint8_t>(fbb.GetBufferPointer(), fbb.GetBufferPointer() + fbb.GetSize());
}

struct SectorSpec {
  std::uint16_t index = 0;
  const char* macro = "";
  const char* name = "";
  const char* owner = "";
  float x = 0, y = 0, z = 0;
};

struct LinkSpec {
  std::uint16_t from = 0;
  std::uint16_t to = 0;
  P::LinkKind kind = P::LinkKind::Gate;
};

std::vector<std::uint8_t> snapshot(std::uint64_t version, const std::vector<SectorSpec>& sectors,
                                   const std::vector<LinkSpec>& links) {
  flatbuffers::FlatBufferBuilder fbb(1024);
  std::vector<flatbuffers::Offset<P::MapSector>> rows;
  rows.reserve(sectors.size());
  for (const auto& sector : sectors) {
    const P::Vec3f pos(sector.x, sector.y, sector.z);
    rows.push_back(P::CreateMapSectorDirect(fbb, sector.index, sector.macro, "cluster", sector.name, sector.owner, &pos));
  }
  std::vector<flatbuffers::Offset<P::MapLink>> link_rows;
  link_rows.reserve(links.size());
  for (const auto& link : links) link_rows.push_back(P::CreateMapLink(fbb, link.from, link.to, link.kind));
  const auto sector_vec = fbb.CreateVector(rows);
  const auto link_vec = fbb.CreateVector(link_rows);
  const auto offset = P::CreateMapSnapshot(fbb, version, sector_vec, link_vec);
  fbb.Finish(offset);
  return finish(fbb);
}

std::vector<std::uint8_t> delta(std::uint64_t version, const std::vector<SectorSpec>& sectors,
                                const std::vector<LinkSpec>& links, const std::vector<std::uint16_t>& removed) {
  flatbuffers::FlatBufferBuilder fbb(1024);
  std::vector<flatbuffers::Offset<P::MapSector>> rows;
  rows.reserve(sectors.size());
  for (const auto& sector : sectors) {
    const P::Vec3f pos(sector.x, sector.y, sector.z);
    rows.push_back(P::CreateMapSectorDirect(fbb, sector.index, sector.macro, "cluster", sector.name, sector.owner, &pos));
  }
  std::vector<flatbuffers::Offset<P::MapLink>> link_rows;
  link_rows.reserve(links.size());
  for (const auto& link : links) link_rows.push_back(P::CreateMapLink(fbb, link.from, link.to, link.kind));
  const auto sector_vec = fbb.CreateVector(rows);
  const auto link_vec = fbb.CreateVector(link_rows);
  const auto removed_vec = fbb.CreateVector(removed);
  const auto offset = P::CreateMapDelta(fbb, version, sector_vec, link_vec, removed_vec);
  fbb.Finish(offset);
  return finish(fbb);
}

// GalaxyMetadata carries SectorInfo (owner_ref, not a faction id) and SectorLink.
std::vector<std::uint8_t> galaxy(const std::vector<SectorSpec>& sectors, const std::vector<LinkSpec>& links) {
  flatbuffers::FlatBufferBuilder fbb(1024);
  std::vector<flatbuffers::Offset<P::SectorInfo>> rows;
  rows.reserve(sectors.size());
  for (const auto& sector : sectors) {
    const P::Vec3f pos(sector.x, sector.y, sector.z);
    rows.push_back(P::CreateSectorInfoDirect(fbb, sector.index, sector.macro, "cluster", sector.name, 7, &pos));
  }
  std::vector<flatbuffers::Offset<P::SectorLink>> link_rows;
  link_rows.reserve(links.size());
  for (const auto& link : links) {
    const P::Vec3f from_pos(0, 0, 0);
    const P::Vec3f to_pos(1, 1, 1);
    link_rows.push_back(P::CreateSectorLink(fbb, link.from, link.to, link.kind, &from_pos, &to_pos));
  }
  const auto sector_vec = fbb.CreateVector(rows);
  const auto link_vec = fbb.CreateVector(link_rows);
  const auto save = fbb.CreateVector(std::vector<std::uint8_t>(32, 0xAB));
  const auto offset = P::CreateGalaxyMetadata(fbb, save, sector_vec, link_vec);
  fbb.Finish(offset);
  return finish(fbb);
}

bool send(M::MapHub& hub, P::MsgType type, const std::vector<std::uint8_t>& payload) {
  return hub.on_frame_message(static_cast<std::uint16_t>(type), std::span<const std::uint8_t>(payload));
}

}  // namespace

TEST_CASE("map: a snapshot replaces the map and carries owners", "[map]") {
  M::MapHub hub;
  hub.reset();
  REQUIRE_FALSE(hub.has_map());
  REQUIRE(hub.source() == M::MapSource::None);

  const auto payload = snapshot(3, {{1, "sector_01", "Argon Prime", "argon", 1, 2, 3}, {2, "sector_02", "Xenon Hub", "xenon", 4, 5, 6}},
                                {{2, 1, P::LinkKind::Gate}});
  REQUIRE(send(hub, P::MsgType::MapSnapshot, payload));
  REQUIRE(hub.has_map());
  REQUIRE(hub.version() == 3);
  REQUIRE(hub.source() == M::MapSource::Live);
  REQUIRE(hub.sector_count() == 2);
  REQUIRE(hub.link_count() == 1);
  REQUIRE(hub.sectors().at(1).owner == "argon");
  REQUIRE(hub.sectors().at(1).name == "Argon Prime");
  REQUIRE(hub.sectors().at(2).x == 4);
  REQUIRE(hub.links().at({1, 2}).from == 1);
  REQUIRE(hub.links().at({1, 2}).to == 2);
  REQUIRE(hub.stats().snapshots == 1);

  REQUIRE(hub.take_dirty());
  REQUIRE_FALSE(hub.take_dirty());

  const auto json = hub.json();
  REQUIRE(json.find("\"source\":\"live\"") != std::string::npos);
  REQUIRE(json.find("\"owner\":\"argon\"") != std::string::npos);
  REQUIRE(json.find("\"index\":1") < json.find("\"index\":2"));
}

TEST_CASE("map: a delta upserts an existing sector, adds and removes links and sectors", "[map]") {
  M::MapHub hub;
  hub.reset();
  REQUIRE(send(hub, P::MsgType::MapSnapshot,
               snapshot(1, {{1, "a", "A", "argon"}, {2, "b", "B", "argon"}, {3, "c", "C", "argon"}},
                        {{1, 2, P::LinkKind::Gate}, {2, 3, P::LinkKind::Gate}})));
  REQUIRE(hub.take_dirty());

  // Sector 2 changes owner, sector 4 appears, the 1-4 link appears, sector 3 disappears with its 2-3 link.
  REQUIRE(send(hub, P::MsgType::MapDelta,
               delta(2, {{2, "b", "B", "xenon"}, {4, "d", "D", ""}}, {{1, 4, P::LinkKind::Highway}}, {3})));
  REQUIRE(hub.version() == 2);
  REQUIRE(hub.source() == M::MapSource::Live);
  REQUIRE(hub.sector_count() == 3);
  REQUIRE(hub.sectors().at(2).owner == "xenon");
  REQUIRE_FALSE(hub.sectors().contains(3));
  REQUIRE(hub.sectors().at(4).index == 4);
  REQUIRE(hub.link_count() == 2);
  REQUIRE(hub.links().contains({1, 2}));
  REQUIRE(hub.links().contains({1, 4}));
  REQUIRE_FALSE(hub.links().contains({2, 3}));
  REQUIRE(hub.stats().deltas == 1);
  REQUIRE(hub.stats().sector_upserts == 2);
  REQUIRE(hub.stats().link_upserts == 1);
  REQUIRE(hub.stats().sectors_removed == 1);
  REQUIRE(hub.take_dirty());
}

TEST_CASE("map: a delta gap drops the map instead of leaving it silently wrong", "[map]") {
  M::MapHub hub;
  hub.reset();
  REQUIRE(send(hub, P::MsgType::MapSnapshot, snapshot(5, {{1, "a", "A", "argon"}}, {})));
  REQUIRE(hub.take_dirty());

  REQUIRE(send(hub, P::MsgType::MapDelta, delta(7, {{1, "a", "A", "xenon"}}, {}, {})));
  REQUIRE_FALSE(hub.has_map());
  REQUIRE(hub.version() == 0);
  REQUIRE(hub.source() == M::MapSource::None);
  REQUIRE(hub.stats().gaps == 1);
  REQUIRE(hub.take_dirty());

  // A delta before any map cannot be applied either.
  REQUIRE(send(hub, P::MsgType::MapDelta, delta(1, {{1, "a", "A", "argon"}}, {}, {})));
  REQUIRE_FALSE(hub.has_map());
  REQUIRE(hub.stats().gaps == 2);
  REQUIRE_FALSE(hub.take_dirty());
}

TEST_CASE("map: stale deltas are ignored", "[map]") {
  M::MapHub hub;
  hub.reset();
  REQUIRE(send(hub, P::MsgType::MapSnapshot, snapshot(2, {{1, "a", "A", "argon"}}, {})));
  REQUIRE(hub.take_dirty());

  REQUIRE(send(hub, P::MsgType::MapDelta, delta(2, {{1, "a", "A", "xenon"}}, {}, {})));
  REQUIRE(send(hub, P::MsgType::MapDelta, delta(1, {{1, "a", "A", "xenon"}}, {}, {})));
  REQUIRE(hub.version() == 2);
  REQUIRE(hub.sectors().at(1).owner == "argon");
  REQUIRE(hub.stats().stale == 2);
  REQUIRE_FALSE(hub.take_dirty());
}

TEST_CASE("map: GalaxyMetadata is the fallback and is ignored once a live map exists", "[map]") {
  M::MapHub hub;
  hub.reset();
  REQUIRE(send(hub, P::MsgType::GalaxyMetadata, galaxy({{1, "a", "A", "argon"}, {2, "b", "B", "argon"}}, {{1, 2, P::LinkKind::Gate}})));
  REQUIRE(hub.has_map());
  REQUIRE(hub.version() == 0);
  REQUIRE(hub.source() == M::MapSource::Checkpoint);
  REQUIRE(hub.sector_count() == 2);
  REQUIRE(hub.sectors().at(1).owner.empty());  // SectorInfo has only owner_ref
  REQUIRE(hub.stats().galaxy == 1);
  REQUIRE(hub.take_dirty());

  REQUIRE(send(hub, P::MsgType::MapSnapshot, snapshot(1, {{1, "a", "A", "argon"}}, {})));
  REQUIRE(hub.source() == M::MapSource::Live);
  REQUIRE(hub.sectors().at(1).owner == "argon");
  REQUIRE(hub.take_dirty());

  REQUIRE(send(hub, P::MsgType::GalaxyMetadata, galaxy({{9, "z", "Z", "argon"}}, {})));
  REQUIRE(hub.stats().galaxy_ignored == 1);
  REQUIRE(hub.version() == 1);
  REQUIRE(hub.source() == M::MapSource::Live);
  REQUIRE(hub.sector_count() == 1);
  REQUIRE(hub.sectors().contains(1));
  REQUIRE_FALSE(hub.take_dirty());
}

TEST_CASE("map: malformed messages are refused and the map survives", "[map]") {
  M::MapHub hub;
  hub.reset();
  REQUIRE(send(hub, P::MsgType::MapSnapshot, snapshot(1, {{1, "a", "A", "argon"}, {2, "b", "B", "argon"}}, {})));
  REQUIRE(hub.take_dirty());

  REQUIRE(send(hub, P::MsgType::MapSnapshot, {}));                       // empty payload
  REQUIRE(send(hub, P::MsgType::MapSnapshot, {1, 2, 3, 4}));             // not a buffer
  REQUIRE(send(hub, P::MsgType::MapSnapshot, snapshot(0, {{1, "a", "A", ""}}, {})));              // version 0
  REQUIRE(send(hub, P::MsgType::MapSnapshot, snapshot(2, {{1, "a", "A", ""}, {1, "b", "B", ""}}, {})));  // duplicate index
  REQUIRE(send(hub, P::MsgType::MapSnapshot, snapshot(2, {{1, "a", "A", ""}}, {{1, 2, P::LinkKind::Gate}})));  // dangling link
  REQUIRE(send(hub, P::MsgType::MapDelta, delta(2, {{0, "a", "A", ""}}, {}, {})));                // zero index
  REQUIRE(send(hub, P::MsgType::MapDelta, delta(2, {{1, "a", "A", ""}}, {{1, 1, P::LinkKind::Gate}}, {})));  // self link
  REQUIRE(send(hub, P::MsgType::MapDelta, delta(2, {{1, "a", "A", ""}, {1, "b", "B", ""}}, {}, {})));  // duplicate index
  REQUIRE(send(hub, P::MsgType::MapDelta, delta(2, {{1, "a", "A", ""}}, {}, {0})));               // removed 0

  REQUIRE(hub.has_map());
  REQUIRE(hub.version() == 1);
  REQUIRE(hub.sector_count() == 2);
  REQUIRE(hub.stats().bad >= 9);
  REQUIRE_FALSE(hub.take_dirty());

  REQUIRE_FALSE(send(hub, P::MsgType::ChatMessage, {}));  // not a map message
}

TEST_CASE("map: reset clears the map, the stats and the dirty flag", "[map]") {
  M::MapHub hub;
  hub.reset();
  REQUIRE(send(hub, P::MsgType::MapSnapshot, snapshot(4, {{1, "a", "A", "argon"}}, {})));
  REQUIRE(hub.take_dirty());

  hub.reset();
  REQUIRE_FALSE(hub.has_map());
  REQUIRE(hub.version() == 0);
  REQUIRE(hub.source() == M::MapSource::None);
  REQUIRE(hub.sector_count() == 0);
  REQUIRE(hub.link_count() == 0);
  REQUIRE(hub.stats().snapshots == 0);
  REQUIRE_FALSE(hub.take_dirty());
  REQUIRE(hub.json().find("\"source\":\"none\"") != std::string::npos);
}
