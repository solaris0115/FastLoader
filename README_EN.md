# FastLoader

FastLoader is a RimWorld startup optimization mod. It improves startup speed by shortening repeated loading work that RimWorld normally performs on every launch when the mod list has not changed.

- RimWorld 1.6
- Requires Harmony

## Performance

Measured with 117 active mods.

| Section | Vanilla | Optimized | Difference |
|---|---:|---:|---:|
| Total time to main menu | 91.8s | 29.9s | -61.8s, 67.4% faster |
| XML cache profile total | 9.2s | 2.1s | -7.0s |
| Texture reload | 53.8s | 4.4s | -49.4s |
| Static atlas | 8.0s | 5.8s | -2.2s |
| PlayDataLoader total | 18.8s | 10.1s | -8.7s |

## Quick Start

1. [Subscribe](#) on Steam Workshop.  
   Or extract the [download](#) file into RimWorld's `Mods` folder.
2. Place FastLoader below Harmony in the mod list. Preferably place it near the top, below Core/DLC and Harmony.
3. Launch RimWorld and run `Build Cache` from `Options -> Mod Settings -> FastLoader`.
4. Restart RimWorld. Startup should be faster from the next launch.

## When to Rebuild Cache

- Mods were added, removed, changed, or manually edited
- Mod load order changed
- Cached mods were updated
- Language changed
- `Compress atlas cache` or `Atlas chunk size` changed in FastLoader settings

## Settings

| Setting | Description |
|---|---|
| `Compress atlas cache` | Compresses static atlas cache with LZ4. Enabled by default. Keep this enabled if disk space is limited or if you want smaller cache files on lower-end systems. Disable it if startup speed is more important, but cache files will become much larger. |
| `Atlas chunk size` | The GPU readback block size used while building atlas cache. Default is 32MB and the allowed range is 4-128MB. If atlas cache build fails or GPU memory is insufficient, lower this value, for example to 64MB, 32MB, or 16MB. Lower values are safer but can make cache build slower. |

## How It Reduces Load Time

FastLoader groups repeated small file reads, text parsing, image decoding, and runtime processing into cache files. Conceptually, it builds bundled loading results once, then reads those bundled results on the next launch.

| Item | Vanilla | FastLoader |
|---|---|---|
| XML | Reads XML from many mods and repeats patching, inheritance, and Def preparation every launch. | Stores the finalized XML as one cache XML file and reduces repeated processing. |
| Language | Parses language XML and injects `DefInjected` data into Defs every launch. | Stores language data and `DefInjected` injection data as binary cache files to reduce text parsing. |
| Texture | Opens many image files individually and decodes PNG/JPG/PSD into Unity texture format. | Caches raw data that has already been converted to Unity texture format, skipping many file reads and image decoding steps. |
| Static atlas | Rebuilds atlas textures, masks, and UV data from loaded textures every launch. | Stores completed atlas textures and UV data in one cache file and reduces atlas baking work. |

## Excluded From Bundles

FastLoader caches mod resources and loading results where repeated startup cost is high. The following are not included in the cache bundles.

- RimWorld Core and official DLC textures: base game resources are already managed through Unity assets or AssetBundles, so they are excluded from the mod texture cache.
- Code-only mods: if a mod only has `Assemblies/` and no XML, language, or texture resources, there is nothing to cache.
- Resources already provided as Unity `AssetBundle`: these do not go through the PNG/JPG decoding path, so they are not part of the raw texture cache.
- Temporary textures or runtime state created while the game is running: FastLoader reuses startup loading results and does not save changing save-game or runtime data.

## How It Works

### XML Cache

Vanilla flow:

1. Read each mod's `Defs/*.xml`.
2. Parse XML and gather it into a form that can produce Defs.
3. Apply XPath patches from `Patches/*.xml`.
4. Resolve XML inheritance.
5. Create Def instances from the final XML nodes.

FastLoader stores the final XML after patch and inheritance processing as a single `Cache/resolved_defs.xml` file. On the next launch, it reduces the overhead of gathering mod XML and reprocessing patches/inheritance, then creates Def instances from the cached XML.

### Language Cache

Vanilla flow:

1. Read `Keyed`, `Strings`, and `DefInjected` files from the selected language and the default language.
2. Parse string files into dictionaries and string lists.
3. Inject `DefInjected` entries into Def instances.

FastLoader stores the parsed language results as `.flang` and `.finj` binary cache files. On the next launch, it reduces language file parsing and applies Def injection and string tables from binary data.

### Texture Cache

Vanilla flow:

1. Traverse each mod's `Textures/` folder.
2. Read and decode PNG/JPG/PSD files.
3. Create Unity `Texture2D`, call `Apply`, and register it in the mod content holder.

FastLoader stores texture data after it has already been converted to Unity texture format as raw texture data in `.texcache`. On the next launch, it reads cached raw data and restores textures with `Texture2D.LoadRawTextureData` instead of opening and decoding many image files again.

### Static Atlas Cache

Vanilla flow:

1. Collect textures that should be included in a static atlas.
2. Blit textures into color and mask atlases.
3. Compress and `Apply` atlas textures.
4. Build UV rects and meshes for each source texture.

FastLoader stores completed atlas texture payloads and UV rects in `.atlascache`. On the next launch, it intercepts `StaticTextureAtlas.Bake`; if a matching cache exists, it restores color/mask textures and tile data, reducing blit, compression, and UV setup work.

## Cache File Structure

Caches are created under the RimWorld config folder.

```text
%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Config\FastLoader\
├── cache_state.xml
├── Cache\
│   ├── manifest.xml
│   └── resolved_defs.xml
├── LanguageCache\
│   ├── language_{folderName}.flang
│   └── definjected_{folderName}.finj
├── TextureCache\
│   ├── mod_{packageid}.texcache
│   ├── group_{groupid}.texcache
│   └── groups.xml
└── StaticAtlasCache\
    └── atlas_{hash}.atlascache
```

### `cache_state.xml`

Stores current cache state, build time, mod loadout hash, language, and whether each cache type was built. It is used to decide whether the cache is valid when the mod list or language changes.

### `Cache/manifest.xml`

```xml
<FastLoaderCache>
  <formatVersion>1</formatVersion>
  <fastLoaderVersion>...</fastLoaderVersion>
  <gameVersion>...</gameVersion>
  <inputHash>...</inputHash>
  <createdUtc>...</createdUtc>
  <entries>
    <entry packageId="mod.package.id" sourceName="Defs/File.xml" />
  </entries>
</FastLoaderCache>
```

`inputHash` is based on game version, FastLoader version, and active mod load order. It does not recursively hash every file in mod folders.

### `Cache/resolved_defs.xml`

The final `Defs` document after patches and XML inheritance have been applied.

```xml
<Defs>
  <ThingDef>...</ThingDef>
  <RecipeDef>...</RecipeDef>
</Defs>
```

### `.texcache`

A `BinaryWriter`-based binary file.

```csharp
struct TextureCacheHeader
{
    byte[4] Magic;          // "FLTX"
    int FormatVersion;      // 1
    byte[64] CacheHash;
    int TextureCount;
    long CreatedUtcTicks;
}

struct RawTextureEntryMeta
{
    string InternalPath;
    string Name;
    int Width;
    int Height;
    int TextureFormat;
    int MipmapCount;
    int FilterMode;
    int AnisoLevel;
    int RawDataLength;
}

byte[] RawData[TextureCount];
```

The file stores header, texture metadata array, then raw texture byte arrays.

### `.flang`

A binary file containing `Keyed` translation dictionary data and `Strings` file contents.

```csharp
struct LanguageCacheHeader
{
    byte[4] Magic;          // "FLLC"
    int FormatVersion;      // 1
    string CacheHash;
    string LanguageFolderName;
    long CreatedUtcTicks;
}

struct KeyedReplacement
{
    string DictKey;
    string Key;
    string Value;
    string FileSource;
    int FileSourceLine;
    string FileSourceFullPath;
    bool IsPlaceholder;
}

struct StringFile
{
    string VirtualPath;
    int LineCount;
    string[] Lines;
}
```

### `.finj`

A binary file containing `DefInjected` translation injection data. Strings are stored in a string table, and each injection references string ids.

```csharp
struct DefInjectedCache
{
    byte[4] Magic;          // "FLDI"
    StringTable Strings;
    int PackageCount;
    DefInjectedPackage[] Packages;
}

struct DefInjectedPackage
{
    int DefTypeStringId;
    int InjectionCount;
    DefInjectedEntry[] Injections;
}

struct DefInjectedEntry
{
    int KeyStringId;
    int PathStringId;
    int NormalizedPathStringId;
    int NonBackCompatiblePathStringId;
    int SuggestedPathStringId;
    int InjectionStringId;
    int FileSourceStringId;
    bool IsPlaceholder;
    int[] FullListStringIds;
    FullListComment[] FullListComments;
}
```

### `.atlascache`

A binary file for restoring static atlases.

```csharp
struct AtlasCacheHeader
{
    int Magic;              // "FLAT" little-endian
    int FormatVersion;      // 6
    string CacheHash;
    int AtlasGroup;
    bool HasMask;
    int TextureCount;
    bool AtlasCacheCompressionEnabled;
    int AtlasCacheChunkSizeMb;
}

struct TexturePayload
{
    string Name;
    int Width;
    int Height;
    int GraphicsFormat;
    int MipCount;
    int FilterMode;
    int WrapMode;
    int AnisoLevel;
    float MipMapBias;
    byte StorageKind;
}
```

`StorageKind`:

| Value | Meaning |
|---:|---|
| `0` | Raw texture data |
| `1` | Raw chunk array |
| `2` | LZ4-compressed raw texture data |
| `3` | LZ4-compressed chunk array |

The atlas body stores color texture payload, optional mask texture payload, and UV rects for source textures. If `Compress atlas cache` is enabled when the cache is built, LZ4 storage is used. If disabled, raw chunk storage is used.

## Mod Settings

Location:

```text
Options -> Mod Settings -> FastLoader
```

| Setting | Description |
|---|---|
| `Enable FastLoader cache` | Enables cache usage |
| `Compress atlas cache` | Enables atlas cache compression. Default ON |
| `Atlas chunk size` | GPU readback block size used while building atlas cache |
| `Build Cache` | Builds XML, language, texture, and atlas caches |
| `Remove Cache` | Deletes all FastLoader caches |
| `Open Cache Folder` | Opens the cache folder |

`solaris.fastloader`
