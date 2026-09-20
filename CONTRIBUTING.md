# Contributing

🌐 **English** · [Español](#contribuir)

Thanks for your interest! This project is a protocol-exact port, so a few ground rules keep it
honest. Please read this page and [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) before opening a PR.

## Ways to help

- **Client UI → 0.99B look** — the biggest open item ([`docs/ui-099b.md`](MuMain-099B/docs/ui-099b.md)).
- **Missing server systems** — see the roadmap in [`docs/STATUS.md`](docs/STATUS.md).
- **Bug reports from real play** — with the class, level, what you did, what you expected and what happened. Server console output and the `LOG/` files help a lot.
- **Documentation and translations.**

## Golden rules

1. **Ported, not invented.** Server behaviour must follow the original SSeMU 0.99B C++ source
   (`Source/Source/Emulator 0.99 (2.1.7)/`). Cite the original file/function in a comment when you
   port a formula. If you cannot verify a number against the source, say so in the PR.
2. **Never hand-write wire structs.** Client packet structs come from `tools/protogen`; server
   packet layouts must match the generated IR (`tools/protogen/audit_muservercs.py`). Padding is
   on the wire — a "close enough" struct silently misreads packets.
3. **Verify against real 0.99B, not against Season 6.** The client codebase contains many
   Season 6 features that do not exist in 0.99B. Before "fixing" missing behaviour, confirm the
   feature exists in the original client (`MuClient/Data/Interface`).
4. **A test that compares an implementation with itself proves nothing.** Expected values must come
   from the original algorithm or from bytes captured from a live server.
5. **Every packet the server can send needs a client receiver that matches its format** (and
   vice-versa). When you add or change one, check both ends — dispatchers pointing at
   later-season handlers are the most common bug in this project.
6. **No game assets, ever** (see [`NOTICE.md`](NOTICE.md)).

## Development workflow

```bash
# server
dotnet build SharpSSeMU/SharpSSeMU.sln
python SharpSSeMU/tests/full_chain_e2e_test.py

# client (from a VS 2022 x86 developer prompt)
cmake --preset windows-x86 -DBUILD_TESTING=ON
cmake --build --preset windows-x86-debug
ctest --test-dir MuMain-099B/out/build/windows-x86 -C Debug
```

Full details: [`docs/BUILDING.md`](docs/BUILDING.md).

### Conventions

- **Client (C++):** follow [`MuMain-099B/docs/CODING_RULES.md`](MuMain-099B/docs/CODING_RULES.md) (also applies to AI-assisted changes; see its `AGENTS.md`). New protocol functions go through the sequence *builder in `Wire099B` → function in `Send099B` → a test pinning the exact bytes → then switch the call site*.
- **Server (C#):** nullable enabled, one concern per commit, comments explain *why* and cite the original source rather than restating the code. Prefer extending existing tables/registries to adding parallel mechanisms.
- **Docs:** user-facing docs are in English with a Spanish counterpart under `docs/es/`; keep both in sync when you change one, or note in the PR that the other needs updating.
- **Commits/PRs:** small, focused; describe the *why*; link the related issue. Target branch: `main`.

## Language

Docs, server logs, the AdminPanel and code comments are English-first; Spanish is a full second language for docs, logs
and the AdminPanel (see `docs/GETTING_STARTED.md`). Write new code and comments in English. A few string literals in
test tooling (console output of `WorldTestClient`, `tests/_env.py`) are still Spanish -- translating them is welcome.

## Reporting security problems

The AdminPanel writes server configuration and the default credentials are for development only.
If you find a security issue, please open a private security advisory on GitHub instead of a public issue.

---

## Contribuir

*(Resumen en español.)* Es un port fiel al protocolo, así que hay reglas básicas:

1. **Portar, no inventar:** el comportamiento del servidor sigue el código C++ original de SSeMU
   0.99B; citá el archivo/función original al portar una fórmula.
2. **Nunca transcribas structs de red a mano:** salen de `tools/protogen`; el relleno viaja por el cable.
3. **Verificá contra 0.99B real, no contra Season 6** (`MuClient/Data/Interface` es la referencia).
4. **Un test que compara una implementación consigo misma no prueba nada.**
5. **Cada paquete que manda el servidor necesita un receptor en el cliente con su mismo formato**, y viceversa.
6. **Nunca subas assets del juego** (ver [`NOTICE.md`](NOTICE.md)).

Flujo de trabajo y compilación: [`docs/es/BUILDING.md`](docs/es/BUILDING.md). Los docs de usuario van en
inglés con contraparte en español en `docs/es/`; mantené ambos sincronizados.
