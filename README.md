# FastLoader

> RimWorld 1.6 시작 로딩 가속 모드  
> 매 실행마다 반복되는 XML 전처리와 텍스처 디코딩을 persistent disk cache로 대체한다.

[![RimWorld](https://img.shields.io/badge/RimWorld-1.6-green.svg)](#)
[![Harmony](https://img.shields.io/badge/requires-Harmony-blue.svg)](#)

## Overview

RimWorld은 게임을 시작할 때마다 아래 두 작업을 처음부터 전부 재실행한다.

1. **Def XML 파이프라인** — 모든 모드의 XML 파일을 디스크에서 읽고, 하나의 문서로 통합하고, XPath 패치를 적용하고, XML 상속(inheritance)을 해석한 뒤, 최종 노드를 C# `Def` 객체로 역직렬화
2. **모드 텍스처 로딩** — 각 모드의 `Textures/` 폴더에서 PNG·JPG 파일을 하나씩 열고, `Texture2D.LoadImage`로 디코딩하고, `Compress` → `Apply`를 수행

모드가 수십~수백 개 설치되면 이 과정에서 수천 개의 파일 I/O와 CPU 연산이 선형으로 증가한다.

FastLoader는 **첫 실행에서 이 전처리 결과를 디스크에 저장**하고, 이후 실행에서는 저장된 결과를 로드해 바닐라 파이프라인의 대부분을 건너뛴다.

| | Vanilla (매 실행) | FastLoader (cache hit) |
|---|---|---|
| **XML** | 파일 읽기 → 통합 → 패치 → 상속 해석 → 역직렬화 | 상속까지 해석된 resolved XML을 읽고 **역직렬화만** 수행 |
| **Textures** | 파일별 open → PNG/JPG 디코딩 → Compress → Apply | AssetBundle에서 **이미 디코딩된** Texture2D를 직접 로드 |

## How It Works

### 1. XML Cache — 전처리된 XML 스냅샷 재사용

캐시 대상은 `Def` 객체가 아니라, 바닐라가 Def를 만들기 직전까지 처리한 **inheritance-resolved XML 문서**다.

```
[cache miss — 첫 실행 또는 모드 구성 변경]

  ① 모든 모드의 Defs/*.xml 디스크 읽기        ← 수천 파일 I/O
  ② 하나의 <Defs> 문서로 통합
  ③ 모든 모드의 Patches/*.xml XPath 적용       ← 무거운 XPath 연산
  ④ XML inheritance 등록 및 해석               ← ParentName 트리 탐색
  ⑤ Def 역직렬화 (XML → C# 객체)
  ⑥ ④까지 완료된 resolved XML을 캐시에 저장     ← FastLoader가 여기서 스냅샷

[cache hit — 다음 실행, 동일 모드 구성]

  ①~④ 전체 스킵
  → 캐시된 resolved XML 로드 (단일 파일 읽기)
  → ⑤ Def 역직렬화만 수행
```

캐시되지 않는 후반부 처리(cross-ref resolve, `Def.ResolveReferences()`, implied Def 생성, shortHash 할당 등)는 바닐라 코드가 그대로 실행한다.

**적용 범위**: Core, DLC, 워크샵, 로컬 등 **모든 활성 모드**의 XML이 대상.

### 2. Texture Cache — AssetBundle로 I/O·디코딩 제거

바닐라에서 모드 텍스처를 로드하는 과정은 다음과 같다.

```
모드별 Textures/ 폴더 순회
→ 파일 하나씩 open + read (수천 회 파일 I/O)
→ new Texture2D + LoadImage(byte[])  (PNG/JPG CPU 디코딩)
→ Texture2D.Compress()               (런타임 압축)
→ Texture2D.Apply()                   (GPU 업로드)
→ ModContentHolder<Texture2D>에 등록
```

FastLoader는 이 텍스처들을 사전에 **Unity AssetBundle**로 패키징한다. 번들 빌드 시점에 이미 디코딩과 임포트가 완료되므로, 런타임에는 번들에서 완성된 `Texture2D`를 꺼내기만 하면 된다.

```
[사전 빌드 — 외부 Unity Editor에서 1회 실행]

  활성 모드의 텍스처 파일 목록 → build input XML 생성
  → Unity Editor 프로젝트 자동 생성
  → BuildPipeline.BuildAssetBundles 실행
  → Uncompressed AssetBundle 출력 (1000개 단위로 분할)

[게임 실행 — cache hit]

  AssetBundle.LoadFromFile (단일 파일 읽기)
  → bundle.LoadAsset<Texture2D> (이미 디코딩된 상태)
  → ModContentHolder<Texture2D>에 주입
  → 바닐라의 파일별 open/decode/compress/apply 경로 우회
```

번들은 `UncompressedAssetBundle`로 빌드된다. 최적화 핵심은 압축이 아니라:
- **N회 파일 I/O → 1회 번들 로드**로 시스템콜 오버헤드 제거
- **런타임 PNG/JPG 디코딩 제거** — 빌드 타임에 이미 처리 완료
- **런타임 Compress/Apply 생략**

**적용 범위**: **워크샵·로컬 모드의 Texture2D만** 대상. Core·DLC 공식 리소스는 이미 Unity 에셋 형태이므로 제외. AudioClip, String 등 다른 콘텐츠 타입은 바닐라 경로 유지.

> **Note**: AssetBundle 빌드에는 `UnityEditor.BuildPipeline`이 필요하다. 이 API는 게임 런타임에 존재하지 않으므로, 외부에 설치된 Unity Editor를 호출해야 한다. 모드 설정에서 빌드 스크립트(cmd/ps1)를 생성할 수 있다.

### 3. Hooking — Harmony Prefix/Postfix

바닐라 로딩 파이프라인에 Harmony 패치로 개입한다.

**XML 경로** — `LoadedModManager`의 6개 메서드를 감싸서, cache hit 시 prefix에서 바닐라 호출을 스킵:

```
LoadModXML             → cache hit: 빈 리스트 반환, XML 파일 읽기 스킵
CombineIntoUnifiedXML  → cache hit: 캐시된 resolved XML 반환
ErrorCheckPatches      → cache hit: 스킵
ApplyPatches           → cache hit: 스킵
ParseAndProcessXML     → cache hit: 캐시 XML에서 직접 Def 역직렬화
ClearCachedPatches     → cache hit: 스킵 + 로드 완료 처리
```

cache miss 시에는 바닐라 로직을 그대로 실행한 뒤, `ParseAndProcessXML` postfix에서 결과 XML을 캐시에 쓴다.

**텍스처 경로** — `ModContentPack.ReloadContentInt`를 가로채서, 해당 모드의 텍스처가 번들 캐시에 있으면 번들에서 로드하고 바닐라 텍스처 로딩을 스킵. 오디오·문자열·기존 AssetBundle 리로드는 바닐라가 처리.

**정리** — `LoadedModManager.ClearDestroy` postfix에서 로드된 번들을 언로드.

### 4. Cache Invalidation

```
input hash = SHA-256(게임 버전 + 활성 모드 수 + 각 모드의 packageId·이름·경로)
```

현재 `SkipInputHashScan = true`로 설정되어 있어, **모드 목록 메타데이터만** 해싱한다. 개별 파일 내용은 해싱하지 않는다. 따라서 모드 내부 파일만 수정하고 모드 목록을 바꾸지 않으면 캐시가 자동 무효화되지 않는다.

무효화가 일어나는 경우:
- 모드 추가·제거·순서 변경
- RimWorld 버전 업데이트
- FastLoader 버전 업데이트
- 설정에서 수동 캐시 초기화

`hotReload` 경로에서는 안전을 위해 캐시를 사용하지 않는다.

## Configuration

**옵션 → 모드 설정 → FastLoader**

| 항목 | 설명 |
|---|---|
| Enable FastLoader cache | XML 캐시 on/off |
| Build all cache | XML 캐시 즉시 기록 + 텍스처 번들 빌드 시작 |
| Prepare resource build | 외부 Unity 빌드용 cmd/ps1 스크립트만 생성 |
| Reset XML cache | XML 캐시 삭제 (다음 로드 시 재생성) |
| Reset resource cache | 텍스처 AssetBundle 캐시 삭제 |

## Runtime Cache Location

캐시는 모드 폴더가 아닌 RimWorld 설정 디렉터리에 생성된다.

```
%APPDATA%\..\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Config\FastLoader\
├── Cache/
│   ├── manifest.xml            # XML 캐시 메타데이터 + input hash
│   └── resolved_defs.xml       # 상속·패치 반영된 최종 XML
└── AssetBundleCache/
    ├── asset_manifest.xml      # 텍스처 번들 메타데이터
    ├── fastloader_assets_win_* # Texture2D AssetBundle 파일들
    ├── asset_build_input.xml   # 빌드 입력 데이터
    ├── run_asset_bundle_build.cmd / .ps1  # 외부 빌드 스크립트
    └── UnityBuildProject/      # 자동 생성된 Unity 프로젝트
```

## Requirements

- [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077)
- (텍스처 캐시 빌드 시) Unity Editor — RimWorld과 동일 Unity 버전 권장

## Project Structure

```
FastLoader/
├── About/
│   └── About.xml                      # 모드 메타데이터 (solaris.fastloader)
├── Assemblies/
│   └── FastLoader.dll                 # 빌드 산출물
├── Contents/                          # DLC별 리소스 루트 (Sounds/, Textures/)
├── Defs/                              # (비어 있음 — 코드 전용 모드)
├── Languages/                         # 번역
├── Source/
│   ├── FastLoader.csproj              # MSBuild 프로젝트 (.NET 4.7.2)
│   ├── FastLoaderMod.cs               # 진입점 — Harmony 설치, 설정 UI
│   ├── FastLoaderSettings.cs          # 모드 설정 (CacheEnabled, ForceRebuild)
│   ├── FastLoaderPatches.cs           # Harmony 패치 정의 (XML 7개 + 텍스처 2개)
│   ├── FastLoaderRuntime.cs           # XML 캐시 HIT/MISS 판정, manifest I/O, input hash 계산
│   ├── FastLoaderXmlParser.cs         # cache hit 시 resolved XML → Def 역직렬화
│   ├── FastLoaderAssetBundleCache.cs  # 텍스처 번들 캐시 관리 + 내장 Unity 빌드 스크립트
│   ├── FastLoaderVirtualFile.cs       # 캐시 파일용 VirtualFile 어댑터
│   └── FastProfile.cs                 # 구간별 프로파일링 (Stopwatch 기반)
└── LoadFolders.xml                    # 버전·DLC별 로드 폴더 규칙
```

---

`solaris.fastloader` · v0.2.2
