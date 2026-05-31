# FastLoader

> RimWorld 1.6 기동 로딩 캐시 모드  
> 반복 실행 때마다 다시 처리되는 resolved XML과 모드 텍스처 로딩 결과를 재사용한다.

[![RimWorld](https://img.shields.io/badge/RimWorld-1.6-green.svg)](#)
[![Harmony](https://img.shields.io/badge/requires-Harmony-blue.svg)](#)
[![Status](https://img.shields.io/badge/status-experimental-orange.svg)](#)

FastLoader는 림월드 시작 로딩 중 특히 비싼 두 구간을 줄이는 모드다.

1. **Def XML 전처리**: XML 파일 읽기, 통합, XPath 패치 적용, XML inheritance 해석.
2. **모드 텍스처 로딩**: PNG/JPG/PSD 파일 열기, 이미지 디코딩, Unity 텍스처 압축, GPU 업로드.

현재 구현은 다음 캐시를 사용한다.

- XML: `resolved_defs.xml`
- 텍스처: raw binary `.texcache`
- 캐시 생성: 모드 설정 화면에서 수동 빌드

현재 FastLoader는 **Unity AssetBundle을 사용하지 않는다.** Unity Editor도 필요 없다.

## 목차

- [빠른 시작](#빠른-시작)
- [현재 상태](#현재-상태)
- [성능 측정 요약](#성능-측정-요약)
- [동작 방식](#동작-방식)
  - [XML 캐시](#xml-캐시)
  - [텍스처 Raw 캐시](#텍스처-raw-캐시)
  - [Harmony Hook 지점](#harmony-hook-지점)
  - [나중에 실행되는 작업과 프로파일링 주의점](#나중에-실행되는-작업과-프로파일링-주의점)
- [캐시 파일 위치](#캐시-파일-위치)
- [설정 화면](#설정-화면)
- [요구 사항](#요구-사항)
- [프로젝트 구조](#프로젝트-구조)
- [빌드](#빌드)
- [프로파일링](#프로파일링)
- [제한사항](#제한사항)
- [제거된 설계: AssetBundle 캐시](#제거된-설계-assetbundle-캐시)

## 빠른 시작

1. Harmony를 설치한다.
2. FastLoader를 Harmony 뒤에 로드한다.
3. FastLoader를 켠 상태로 림월드를 한 번 실행한다.
4. `Options -> Mod Settings -> FastLoader`를 연다.
5. `Build XML cache now`를 누른다.
6. `Build texture cache now`를 누른다.
7. 림월드를 재시작한다.
8. 설정 화면이나 프로파일 로그에서 캐시 HIT 상태를 확인한다.

캐시가 제대로 적용되면 startup profile metadata에 대략 다음 값이 나온다.

```text
fastLoader.xmlCacheStatus = HIT
optimization.xmlCachingApplied = true
optimization.texturePrecacheApplied = true
fastLoader.textureRawCacheHitCount > 0
```

캐시 파일이 없거나 무효하면 FastLoader는 해당 구간만 바닐라 로딩 경로로 되돌아간다.

## 현재 상태

| 영역 | 상태 | 설명 |
|---|---|---|
| XML 캐시 | 동작 중 | inheritance까지 해석된 resolved XML을 재사용한다. |
| 텍스처 캐시 | 동작 중 | AssetBundle이 아니라 raw `.texcache`를 사용한다. |
| 캐시 생성 | 수동 | 게임 로딩 후 설정 화면에서 빌드 버튼을 눌러 생성한다. |
| 캐시 무효화 | 부분 지원 | 게임 버전, FastLoader 버전, 활성 모드 메타데이터를 해싱한다. 파일 내용 전체 스캔은 기본 비활성이다. |
| StartupProfiler 연동 | 개발 환경에서 사용 중 | `StartupProfilerTools`가 RimWorld core code에 계측 코드를 주입한다. |
| AssetBundle 캐시 | 제거됨 | Unity Editor, AssetBundle, 외부 리소스 빌더 관련 설명은 과거 설계다. |

## 성능 측정 요약

측정 로그 위치:

```text
StartupProfilerLogs/
```

대표 실행 결과:

| 로그 | 캐시 상태 | 메인 메뉴까지 | 비고 |
|---|---|---:|---|
| `startup_profile_20260531_044627_646.md` | XML MISS, 텍스처 바닐라 경로 | 69.73초 | 기준 실행 |
| `startup_profile_20260531_045130_321.md` | XML HIT, 텍스처 raw cache HIT | 42.53초 | 현재 관측된 최선 실행 |
| `startup_profile_20260531_200137_253.md` | XML HIT, 텍스처 raw cache HIT, miss 6개 | 53.34초 | 최신 실행. static constructor와 atlas 구간이 더 느렸음 |

FastLoader가 직접 줄이는 구간:

| 구간 | 기준 / MISS | 캐시 HIT | 설명 |
|---|---:|---:|---|
| FastLoader XML profile | 5.57초 | 최신 2.49초 | cached XML을 읽어도 `Def` 역직렬화는 수행한다. |
| `ModContentPack.ReloadContent` deferred content action | 25.98초 | 최선 2.62초, 최신 5.62초 | 텍스처 raw cache가 PNG/JPG 디코딩 대부분을 제거한다. |
| vanilla texture decode rollup | 18,636 scope / 48.35초 inclusive | 최신 4 scope / 0초 수준 | 텍스처 raw cache 적용 여부 확인용 지표. |

전체 기동 시간은 FastLoader 밖의 요인에 크게 흔들린다.

- `StaticConstructorOnStartupUtility.CallAll`
- `StaticTextureAtlas.ApplyTextureCompression`
- `StaticTextureAtlas.BlitTexturesToColorAtlas`
- `AlienRace.HarmonyPatches` 같은 타 모드 static constructor

따라서 전체 시간만 보지 말고 XML, texture reload, atlas, static constructor를 분리해서 봐야 한다.

## 동작 방식

### XML 캐시

림월드 바닐라 XML 로딩 흐름:

```text
LoadModXML
-> CombineIntoUnifiedXML
-> ErrorCheckPatches
-> ApplyPatches
-> ParseAndProcessXML
   -> XmlInheritance 해석
   -> Def 역직렬화
```

FastLoader는 XPath 패치와 XML inheritance 해석까지 끝난 XML 상태를 저장한다. 최종 `Def` 객체를 저장하는 것이 아니다.

Cache MISS 흐름:

```text
모든 Defs/*.xml 읽기
-> 하나의 <Defs> 문서로 통합
-> Patches/*.xml XPath 적용
-> XML inheritance 해석
-> Def 객체 역직렬화
-> resolved XML snapshot을 메모리에 보관
-> 사용자가 "Build XML cache now"를 누르면 디스크에 저장
```

Cache HIT 흐름:

```text
Cache/manifest.xml 읽기
-> Cache/resolved_defs.xml 읽기
-> LoadModXML / CombineIntoUnifiedXML / ErrorCheckPatches / ApplyPatches 스킵
-> cached resolved XML에서 Def 객체 역직렬화
```

캐시 HIT에서도 림월드 후반부 처리는 그대로 실행된다.

- cross-reference resolve
- `Def.ResolveReferences()`
- implied Def 생성
- short hash 할당
- 각종 mod post-load action

### 텍스처 Raw 캐시

림월드 바닐라 모드 텍스처 로딩 흐름:

```text
ModContentPack.ReloadContentInt
-> ModContentHolder<Texture2D>.ReloadAll
-> PNG/JPG/PSD 파일별 open/read
-> Texture2D.LoadImage
-> Texture2D.Compress
-> Texture2D.Apply
-> ModContentHolder<Texture2D>에 등록
```

FastLoader는 이미 압축된 raw texture bytes를 `.texcache`에 저장한다.

캐시 빌드 흐름:

```text
사용자가 "Build texture cache now" 클릭
-> 현재 로드된 Texture2D 수집
-> RenderTexture + ReadPixels로 GPU readback
-> Compress
-> GetRawTextureData
-> TextureCache/mod_{packageid}.texcache 저장
```

Cache HIT 흐름:

```text
Patch_ModContentPackReloadContentInt_TextureCache.Prefix
-> audioClips.ReloadAll       (바닐라)
-> LoadRawTextureData         (FastLoader 텍스처 캐시)
-> strings.ReloadAll          (바닐라)
-> assetBundles.ReloadAll     (바닐라)
```

Cache MISS 흐름:

```text
해당 모드의 유효한 .texcache 없음
-> 바닐라 ReloadContentInt 실행
```

Core와 공식 DLC 텍스처는 캐시 대상에서 제외된다. 주 대상은 워크샵/로컬 모드 텍스처다.

### Harmony Hook 지점

XML 경로:

| 대상 | Cache HIT 동작 |
|---|---|
| `LoadedModManager.LoadModXML` | 빈 리스트 반환, XML 파일 읽기 스킵 |
| `LoadedModManager.CombineIntoUnifiedXML` | cached `XmlDocument` 반환 |
| `LoadedModManager.ErrorCheckPatches` | 스킵 |
| `LoadedModManager.ApplyPatches` | 스킵 |
| `LoadedModManager.ParseAndProcessXML` | cached resolved XML에서 직접 역직렬화 |
| `LoadedModManager.ClearCachedPatches` | FastLoader XML profile 완료 처리 |

텍스처 경로:

| 대상 | 동작 |
|---|---|
| `ModContentPack.ReloadContentInt` | 해당 모드의 raw cache가 있을 때 텍스처 부분만 대체 |
| `LoadedModManager.ClearDestroy` | 로드된 texture cache 상태 초기화 |

프로파일링 보조 hook:

| 대상 | 동작 |
|---|---|
| `AlienRace.AlienHarmony.Patch` | `StartupProfilerCore`가 있을 때 patch 대상별 시간을 기록. 없으면 사실상 no-op |

### 나중에 실행되는 작업과 프로파일링 주의점

림월드 로딩에는 “지금 호출한 함수가 실제 일을 나중에 예약만 하는” 구조가 있다.

대표 예:

```text
ModContentPack.ReloadContent
-> LongEventHandler.ExecuteWhenFinished(delegate { ReloadContentInt(hotReload); })
```

따라서 `ReloadContent` 메서드 자체를 감싼 시간은 실제 텍스처/오디오/문자열 로딩 시간이 아니다. 실제 작업은 나중에 LongEvent action에서 실행된다.

로그를 읽을 때 기준:

- deferred action은 `longEventInternalActionSummary_inclusive`를 본다.
- `ModContentPack.ReloadContent lambda b__0`가 실제 content reload body다.
- `PlayDataLoader.DoPlayLoad lambda b__4_4`는 static constructor, atlas bake, GC 등 여러 작업을 포함한다.
- `ModContentPack.LoadDefs`는 iterator다. iterator 생성 scope만 보고 XML 전체 시간으로 해석하면 안 된다.

## 캐시 파일 위치

FastLoader 캐시는 림월드 설정 폴더에 생성된다.

```text
%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Config\FastLoader\
├── Cache\
│   ├── manifest.xml
│   └── resolved_defs.xml
└── TextureCache\
    ├── mod_{packageid}.texcache
    ├── group_{groupid}.texcache
    └── groups.xml
```

현재 테스트 환경에서 관측된 크기:

| 캐시 | 파일 수 | 크기 |
|---|---:|---:|
| XML cache | 2개 | 약 24.5 MB |
| Texture raw cache | 58개 `.texcache` | 약 932 MB |

큰 텍스처 캐시 예:

| 파일 | 크기 |
|---|---:|
| `mod_vanillaexpanded.vpsycastse.texcache` | 261.96 MB |
| `mod_vanillaexpanded.vfepropsanddecor.texcache` | 108.02 MB |
| `mod_vanillaexpanded.vgeneticse.texcache` | 90.71 MB |

## 설정 화면

위치:

```text
Options -> Mod Settings -> FastLoader
```

| 항목 | 설명 |
|---|---|
| `Enable FastLoader cache` | XML/텍스처 캐시 사용 여부 |
| `Reset all caches` | XML + 텍스처 캐시 전체 삭제 |
| `Reset XML cache` | `Cache/manifest.xml`, `Cache/resolved_defs.xml` 삭제 |
| `Reset texture cache` | `.texcache` 파일 삭제 |
| `Build texture cache now` | 현재 메모리에 로드된 모드 텍스처를 `.texcache`로 저장 |
| `Build XML cache now` | 현재 로딩에서 확보한 resolved XML snapshot을 저장 |
| `Texture cache: ...` | 파일 수, 총 크기, hit/miss 수 표시 |
| `Cache misses: Show/Hide` | 현재 실행에서 texture cache miss가 난 packageId 표시 |

모드 구성을 바꾼 뒤 권장 흐름:

```text
Reset all caches
-> 재시작 또는 바닐라 경로로 한 번 로딩
-> FastLoader 설정 열기
-> Build XML cache now
-> Build texture cache now
-> 림월드 재시작
```

## 요구 사항

- RimWorld 1.6
- Harmony (`brrainz.harmony`)
- .NET Framework 4.7.2 build target

현재 raw texture cache 방식은 Unity Editor가 필요 없다.

## 프로젝트 구조

```text
FastLoader/
├── README.md
├── REPORT_CURRENT_STATUS.md
└── Project/
    ├── About/
    │   └── About.xml
    ├── Assemblies/
    │   └── FastLoader.dll
    ├── Contents/
    ├── Defs/
    ├── Languages/
    ├── LoadFolders.xml
    ├── README.md
    └── Source/
        ├── FastLoader.csproj
        ├── FastLoaderMod.cs
        ├── FastLoaderPatches.cs
        ├── FastLoaderRuntime.cs
        ├── FastLoaderSettings.cs
        ├── FastLoaderXmlParser.cs
        ├── FastProfile.cs
        ├── RawTextureEntry.cs
        ├── TextureRawCache.cs
        ├── TextureCacheGroup.cs
        ├── TextureCapturePatches.cs
        ├── StartupProfilerBridge.cs
        └── HarmonyPatchProfiler.cs
```

주요 파일:

| 파일 | 역할 |
|---|---|
| `FastLoaderMod.cs` | RimWorld Mod 진입점, Harmony 설치, 설정 UI |
| `FastLoaderPatches.cs` | XML/텍스처 로딩 경로 Harmony patch |
| `FastLoaderRuntime.cs` | XML 캐시 상태, manifest I/O, input hash, profile 상태 |
| `FastLoaderXmlParser.cs` | cached XML -> `Def` 역직렬화 |
| `TextureRawCache.cs` | `.texcache` 읽기/쓰기/삭제/리빌드, hit/miss 추적 |
| `RawTextureEntry.cs` | raw texture entry 모델과 binary format reader/writer |
| `TextureCacheGroup.cs` | 선택적 grouped texture cache 메타데이터 |
| `TextureCapturePatches.cs` | placeholder. 자동 텍스처 캡처는 제거됨 |
| `FastProfile.cs` | FastLoader 내부 stopwatch profile |
| `StartupProfilerBridge.cs` | `StartupProfilerCore` reflection bridge |
| `HarmonyPatchProfiler.cs` | AlienRace Harmony patch 분석용 추가 계측 |

## 빌드

FastLoader 빌드:

```powershell
dotnet build FastLoader\Project\Source\FastLoader.csproj -c Release
```

출력:

```text
FastLoader\Project\Assemblies\FastLoader.dll
```

`FastLoader.csproj`는 로컬 Steam 설치 경로의 RimWorld/Harmony assembly를 참조한다. 설치 경로가 다르면 `HintPath`를 수정해야 한다.

## 프로파일링

FastLoader 자체는 StartupProfiler 없이도 동작한다. 이 워크스페이스의 상세 기동 프로파일링은 별도 도구가 담당한다.

```text
StartupProfilerTools/
├── StartupProfilerCore/
└── StartupProfilerInjector/
```

로그 위치:

```text
StartupProfilerLogs/
├── startup_profile_*.md
└── fastloader_cache_profile_*.md
```

자주 보는 섹션:

| 섹션 | 용도 |
|---|---|
| `metadata` | XML/texture cache HIT 여부 확인 |
| `exclusiveSummary_nonOverlapping` | 병목 우선순위 파악 |
| `inclusiveSummary_reference` | 원본 scope duration 확인. 부모/자식 시간이 겹칠 수 있음 |
| `vanillaTexturePipelineStages` | 바닐라 텍스처 decode가 스킵됐는지 확인 |
| `staticConstructorSummary` | 느린 static constructor 확인 |
| `longEventInternalActionSummary_inclusive` | 나중에 실행된 deferred action 확인 |
| `modFeatureSummary` | 모드별 비용 분해 |

중요 metadata key:

```text
optimization.xmlCachingApplied
optimization.texturePrecacheApplied
fastLoader.xmlCacheStatus
fastLoader.xmlLoadElapsed
fastLoader.textureRawCacheHitCount
fastLoader.textureRawCacheMissCount
fastLoader.textureRawCacheHitEntries
fastLoader.textureRawCacheTotalBytes
```

## 제한사항

### 캐시 생성은 수동

FastLoader는 일반 로딩 중 캐시 파일을 자동 생성하지 않는다. 사용자가 설정 화면에서 직접 빌드해야 한다.

이유: 과거 자동 텍스처 캡처가 오디오 등 비텍스처 로딩에 간섭한 적이 있어, 현재는 normal load를 최대한 바닐라에 가깝게 유지한다.

### Input Hash는 빠르지만 완전히 보수적이지 않음

현재 XML/텍스처 캐시 해시는 다음 메타데이터를 사용한다.

```text
게임 버전
+ FastLoader 버전
+ 활성 모드 목록
+ 각 모드 packageId/name/root path/order
```

파일 내용 전체는 기본적으로 스캔하지 않는다. 모드 XML/텍스처 파일만 바뀌고 package/path/order가 그대로면 수동으로 캐시를 reset/rebuild해야 한다.

### 텍스처 캐시는 클 수 있음

`.texcache`는 압축된 raw texture data를 저장하지만, 대형 모드팩에서는 수백 MB에서 1GB 이상까지 커질 수 있다.

### Texture Cache Miss는 정상적으로 발생할 수 있음

아직 캐시를 만들지 않은 모드, 제외 대상 모드, validation 실패 모드는 바닐라 텍스처 로딩으로 돌아간다.

### Mipmap 제한

현재 raw texture cache는 mip level 1개를 저장한다. 다중 mipmap을 기대하는 텍스처에서는 바닐라 결과와 차이가 날 수 있다.

### Core/공식 DLC 텍스처 제외

FastLoader 텍스처 캐시는 워크샵/로컬 모드 텍스처를 대상으로 한다. Core와 공식 DLC 리소스는 대부분 Unity asset 형태라 여기서 최적화하지 않는다.

### 전체 기동 시간은 노이즈가 큼

FastLoader가 제어하지 않는 static constructor, texture atlas bake, GC, 타 모드 초기화 때문에 전체 시간은 실행마다 몇 초 이상 흔들릴 수 있다.

## 제거된 설계: AssetBundle 캐시

예전 문서나 보고서에는 다음 항목이 등장할 수 있다.

- `FastLoaderAssetBundleCache`
- `FastLoaderVirtualFile`
- `FastLoaderResourceBuilder`
- `AssetBundleCache/`
- Unity Editor build script
- `BuildPipeline.BuildAssetBundles`

이 설계는 제거됐다.

현재 텍스처 캐시는 아래 방식이다.

```text
TextureCache/mod_{packageid}.texcache
Texture2D.LoadRawTextureData
Texture2D.Apply(false, true)
```

AssetBundle 관련 설명은 과거 설계 기록으로만 보면 된다.

---

`solaris.fastloader` / FastLoader v0.2.2 / RimWorld 1.6
