## Summary

<!-- What does this PR do, and why? -->

## Area

- [ ] Server (`SharpSSeMU`)
- [ ] Client (`MuMain-099B`)
- [ ] Docs / tooling

## Verification

<!-- Server behaviour must be ported from the original 0.99B source. Cite the original file/function,
     or say what you could not verify. For protocol changes, state that both ends (server sender and
     client receiver) were checked. -->

- Original source referenced: <!-- e.g. Attack.cpp CAttack::MissCheck -->
- [ ] `dotnet build SharpSSeMU/SharpSSeMU.sln` passes (server changes)
- [ ] `ctest -C Debug` passes (client changes)
- [ ] Tested against a real client, or explained why not

## Checklist

- [ ] No game assets or proprietary data are included (see `NOTICE.md`)
- [ ] Packet structs come from `tools/protogen`, not transcribed by hand
- [ ] English docs updated (and `docs/es/` counterpart, or noted as needing an update)
- [ ] Focused diff, one concern per commit
