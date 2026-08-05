# ElkUtils

Admin teleport utilities for tamed elk in Vintage Story - bring your elk to you, teleport to your elk, send it to another player, fetch a player to it.

| | |
|---|---|
| modid | `lionfoxelkutils` |
| version | 1.0.0 |
| game dependency | 1.21.6 |
| type | code (C#) |

## Building

Requires the Vintage Story install to reference the game assemblies. Set the
`VINTAGE_STORY` environment variable to your install directory, then:

```
dotnet build -c Release
```

Or override per-invocation: `dotnet build -c Release -p:VS_PATH=D:\Vintagestory`

## Status

Deployed on the server at v1.0.0. **Not yet ported to Vintage Story 1.22.x** â€”
1.22 requires .NET 10 and changed parts of the modding API. Porting is tracked
in the private `vintagestory-server-ops` repo (`registry/own-mods.md`).

## History

This mod was built and deployed before it had a repository of its own; its source
lived only inside the `lionfox_servermods` working mirror until 2026-08-05.
