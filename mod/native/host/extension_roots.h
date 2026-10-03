#pragma once
// host/extension_roots: the thin Windows side of core/mods (M2-03). Builds the ExtensionRoots core/mods scans.
// Pure path arithmetic: no file I/O, so it is safe on any thread.
//
//   install    <X4 dir>\extensions
//   user       <Documents>\Egosoft\X4            (core/mods scans <profile>\extensions and extensions below it)
//   workshop   <X4 dir>\..\..\workshop\content\392160   (X4 lives in steamapps\common\<X4 folder>)
//
// Wiring into the DLL (later feature, see mod/native/core/mods/mods.h): in the feature's init
//   auto roots = host::make_extension_roots(host::x4_install_dir());           // documents defaults to documents_dir()
//   provider = std::make_unique<mods::ExtensionProvider>(mods::ProviderOptions{roots, config_dir / "ext-hash-cache.json"});
//   mods::mark_frame_thread(true);  // in on_frame_update's first call (and for the thread that runs Connect)
//   provider->start();              // at start-menu time; the worker does all the file I/O
//   provider->set_reported(parse(x4mp.extensions json from Lua));   // M2-X1 bridge, any thread
//   session_options.extensions = provider.get();                    // Session::start() calls snapshot() (waits <= 2 s)

#include <filesystem>

#include "core/mods/mods.h"

namespace x4mp::host {

// The directory of the running X4 executable (GetModuleFileNameW(nullptr)); empty on failure.
[[nodiscard]] std::filesystem::path x4_install_dir();

// `documents` empty => documents_dir().
[[nodiscard]] mods::ExtensionRoots make_extension_roots(const std::filesystem::path& x4_dir,
                                                        const std::filesystem::path& documents = {});

}  // namespace x4mp::host
