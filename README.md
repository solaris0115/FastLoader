# FastLoader

림월드의 게임 기동 속도를 개선하는 모드입니다. 모드를 바꾸지 않았지만 매번 게임 실행마다 반복 로드하는 행위를 단축시켜 속도를 개선했습니다.

- RimWorld 1.6
- Harmony 필요

## 성능 개선 지표

측정 기준: 활성 모드 117개

| 구간 | 바닐라 | 개선 | 차이 |
|---|---:|---:|---:|
| 전체 로비 진입 | 91.8초 | 29.9초 | -61.8초, 67.4% 감소 |
| XML 캐시 프로파일 전체 | 9.2초 | 2.1초 | -7.0초 |
| 텍스처 리로드 | 53.8초 | 4.4초 | -49.4초 |
| 정적 아틀라스 | 8.0초 | 5.8초 | -2.2초 |
| PlayDataLoader 전체 | 18.8초 | 10.1초 | -8.7초 |

## 빠른 시작

1. Steam 창작마당에서 [구독](#)한다.  
   또는 [다운로드](#) 파일을 림월드 `Mods` 폴더에 압축 해제.
2. 모드 배열에서 Harmony 아래에 둔다. 가능하면 Core/DLC 바로 아래쪽의 앞부분에 배치.
3. 림월드 실행 후 `Options -> Mod Settings -> FastLoader`에서 `Build Cache`를 실행.
4. 림월드를 재시작하면 다음 실행부터 속도가 빨라집니다.

## 캐시를 다시 만들어야 하는 경우

- 모드 추가, 제거, 변경, 직접 수정
- 모드 배열 순서 변경
- 캐시된 모드 업데이트
- 언어 변경
- FastLoader 설정에서 atlas 압축 여부나 atlas chunk size 변경

## 세부 항목

| 항목 | 설명 |
|---|---|
| `Compress atlas cache` | 정적 아틀라스 캐시를 LZ4로 압축한다. 기본값은 ON이다. 하드 용량이 부족하거나 저사양 환경에서 큰 캐시 파일을 피하고 싶으면 켜둔다. 속도를 우선하면 끌 수 있지만 캐시 파일이 크게 증가한다. |
| `Atlas chunk size` | 아틀라스 캐시를 만들 때 GPU에서 한 번에 읽어오는 block 크기다. 기본값은 32MB이고 4~128MB 사이로 조정할 수 있다. 아틀라스 캐시 빌드 중 GPU 메모리 부족이나 실패가 발생하면 64MB, 32MB, 16MB처럼 낮춘다. 낮을수록 안전하지만 빌드는 느려질 수 있다. |

## 어떻게 줄어드는가

FastLoader는 매 실행마다 반복되는 작은 파일 읽기, 텍스트 파싱, 이미지 디코딩, 런타임 가공 단계를 캐시 파일로 묶어 재사용한다. 개념적으로는 여러 모드의 로딩 결과를 한 번 빌드한 뒤, 다음 실행부터는 번들화된 결과물을 읽는 방식이다.

| 항목 | 바닐라 | FastLoader |
|---|---|---|
| XML | 여러 모드의 XML을 매번 읽고 patch, inheritance, Def 생성 준비를 다시 수행한다. | 수정이 끝난 최종 XML을 하나의 캐시 XML로 묶어 저장하고 반복 처리 비용을 줄인다. |
| 언어 | 언어 XML을 매번 파싱하고 `DefInjected`를 Def에 주입한다. | 언어 데이터와 `DefInjected` 주입 정보를 바이너리로 저장해 텍스트 파싱 비용을 줄인다. |
| 텍스처 | 수많은 이미지 파일을 개별 open/read하고 PNG/JPG/PSD를 Unity 텍스처 포맷으로 디코딩한다. | Unity 텍스처 포맷으로 변환된 raw data를 통째로 캐싱해 여러 파일 입출력과 디코딩 과정을 생략한다. |
| 정적 아틀라스 | 로드된 텍스처를 다시 모아 atlas texture, mask, UV 정보를 매번 만든다. | 완성된 atlas texture와 UV 정보를 하나의 캐시 파일로 저장해 bake 과정을 줄인다. |

## 번들에서 제외되는 내용

FastLoader는 반복 비용이 큰 모드 리소스와 로딩 결과를 중심으로 캐시한다. 다음 항목은 캐시 번들에 포함하지 않는다.

- RimWorld Core와 공식 DLC 텍스처: 기본 게임 리소스는 이미 Unity asset/AssetBundle 쪽으로 관리되므로 모드 텍스처 캐시 대상에서 제외한다.
- 코드 전용 모드: `Assemblies/`만 있거나 실제 XML, 언어, 텍스처 리소스가 없는 경우 캐시할 파일이 없다.
- 이미 Unity `AssetBundle`로 제공되는 리소스: PNG/JPG 디코딩 경로를 타지 않으므로 텍스처 raw 캐시 대상이 아니다.
- 실행 중 새로 생성되는 임시 텍스처나 런타임 상태: 시작 로딩 결과를 재사용하는 목적이므로 세이브/플레이 중 변화하는 데이터는 저장하지 않는다.

## 동작 방식

### XML 캐시

기존 흐름:

1. 각 모드의 `Defs/*.xml`을 읽는다.
2. XML을 파싱하고 Def를 만들 수 있는 형태로 모은다.
3. `Patches/*.xml`의 XPath patch를 적용한다.
4. XML inheritance를 해석한다.
5. 최종 XML 노드에서 Def 인스턴스를 만든다.

FastLoader는 patch와 inheritance 적용까지 끝난 최종 XML 자체를 `Cache/resolved_defs.xml` 하나로 저장한다. 다음 실행에서는 모드별 XML 파일을 다시 모으고 patch/inheritance를 다시 처리하는 오버헤드를 줄이고, 캐시된 XML에서 Def 인스턴스를 만든다.

### 언어 캐시

기존 흐름:

1. 선택 언어와 기본 언어의 `Keyed`, `Strings`, `DefInjected` 파일을 읽는다.
2. 문자열 파일을 파싱해서 딕셔너리와 문자열 목록을 만든다.
3. `DefInjected` 항목을 Def 인스턴스에 주입한다.

FastLoader는 매번 텍스트 파싱 후 `DefInjected`를 구성하던 결과를 `.flang`, `.finj` 바이너리 캐시로 저장한다. 다음 실행에서는 언어 파일 파싱 과정을 줄이고 바이너리 데이터를 바로 읽어 Def 주입과 문자열 테이블 구성을 빠르게 처리한다.

### 텍스처 캐시

기존 흐름:

1. 모드별 `Textures/` 폴더를 순회한다.
2. PNG/JPG/PSD 파일을 읽고 디코딩한다.
3. Unity `Texture2D`를 만들고 `Apply`한 뒤 모드 content holder에 등록한다.

FastLoader는 한 번 Unity 텍스처 포맷으로 변환된 정보를 raw texture data로 `.texcache`에 저장한다. 다음 실행에서는 여러 이미지 파일을 다시 열고 디코딩하는 대신, 캐시된 raw data를 읽어 `Texture2D.LoadRawTextureData`로 복원한다.

### 정적 아틀라스 캐시

기존 흐름:

1. 아틀라스 대상 텍스처 목록을 모은다.
2. color atlas와 mask atlas에 텍스처를 blit한다.
3. 아틀라스 텍스처 압축과 `Apply`를 수행한다.
4. 각 텍스처에 대응하는 UV rect와 mesh를 만든다.

FastLoader는 완성된 atlas texture payload와 UV rect 목록을 `.atlascache`로 저장한다. 다음 실행에서는 `StaticTextureAtlas.Bake`를 가로채 캐시가 맞으면 color/mask texture와 tile 정보를 복원하고, blit/압축/UV 구성 과정을 줄인다.

## 캐시 파일 구조

캐시는 림월드 설정 폴더 아래에 생성된다.

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

현재 캐시 상태, 빌드 시점, 모드 배열 해시, 언어, 각 캐시 종류별 생성 여부를 기록한다. 모드 배열이나 언어가 달라지면 캐시 사용 여부 판단에 사용된다.

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

`inputHash`는 게임 버전, FastLoader 버전, 활성 모드 배열을 기준으로 만든다. 모드 파일 전체를 재귀 순회해 내용 해시를 만들지는 않는다.

### `Cache/resolved_defs.xml`

Patch와 XML inheritance가 적용된 뒤의 최종 `Defs` 문서다.

```xml
<Defs>
  <ThingDef>...</ThingDef>
  <RecipeDef>...</RecipeDef>
</Defs>
```

### `.texcache`

`BinaryWriter` 기반 바이너리 파일이다.

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

저장 순서는 header, texture metadata 배열, raw texture byte 배열이다.

### `.flang`

`Keyed` 번역 딕셔너리와 `Strings` 파일 내용을 저장하는 바이너리 파일이다.

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

`DefInjected` 번역 주입 데이터를 저장하는 바이너리 파일이다. 문자열은 string table에 모아두고, 각 injection은 string id를 참조한다.

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

정적 아틀라스 복원용 바이너리 파일이다.

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

| 값 | 의미 |
|---:|---|
| `0` | raw texture data |
| `1` | raw chunk 배열 |
| `2` | LZ4 압축 raw texture data |
| `3` | LZ4 압축 chunk 배열 |

아틀라스 본문에는 color texture payload, optional mask texture payload, texture별 UV rect 배열이 저장된다. `Compress atlas cache`가 켜져 있으면 LZ4 저장을 사용하고, 꺼져 있으면 raw chunk 저장을 사용한다.

## 모드 설정

위치:

```text
Options -> Mod Settings -> FastLoader
```

| 항목 | 설명 |
|---|---|
| `Enable FastLoader cache` | 캐시 사용 여부 |
| `Compress atlas cache` | 아틀라스 캐시 압축 여부. 기본 ON |
| `Atlas chunk size` | 아틀라스 캐시 빌드 중 GPU에서 한 번에 읽는 block 크기 |
| `Build Cache` | XML, 언어, 텍스처, 아틀라스 캐시 생성 |
| `Remove Cache` | 모든 FastLoader 캐시 삭제 |
| `Open Cache Folder` | 캐시 폴더 열기 |

`solaris.fastloader`
