# FastLoader

림월드 로딩 개선 모드입니다.
게임 실행마다 발생하는 텍스처 로딩과 XML 파싱과 같은 오버헤드를 개선하여 로딩 속도를 개선합니다.
정리된 Defs를 캐싱하여 데이터 접근 속도를 높이고 텍스처를 일괄로 묶어 초기 로딩 속도를 개선합니다.

[![RimWorld](https://img.shields.io/badge/RimWorld-1.6-green.svg)](#)
[![Harmony](https://img.shields.io/badge/requires-Harmony-blue.svg)](#)

## 성능 개선 지표

테스트 환경: 활성 모드 117개 기준

| 구간 | 개선 전 | 개선 후 |
|---|---:|---:|
| 전체 로드 | 106.64초 | 55.61초 |
| XML 파싱 및 patch 적용 | 10.29초 | 3.01초 |
| 텍스처 로드/디코딩 | 45.09초 | 2.50초 |

## 빠른 시작

1. 다운로드한 파일을 림월드 `Mods` 폴더에 압축 해제한다.  
   또는 Steam 창작마당에서 구독한다.
2. 모드 배열에서 Harmony 뒤에 둔다. 가능하면 Core/DLC와 Harmony 바로 아래, 다른 대형 모드들보다 위에 배치한다.
3. 림월드를 실행한 뒤 `Options -> Mod Settings -> FastLoader`에서 캐시를 빌드한다.
   - `Build XML cache now`
   - `Build texture cache now`
4. 림월드를 재시작한다.
5. 다음 재시작부터 실행 속도가 상승한다.

캐시 파일이 없거나 모드 구성이 바뀌면 해당 구간은 기본 로딩 방식으로 실행된다. 모드 목록을 바꾼 뒤에는 FastLoader 설정에서 캐시를 다시 빌드하는 것이 좋다.

## 캐시를 다시 만들어야 하는 경우

다음 상황에서는 설정 화면에서 캐시를 다시 빌드하는 것을 권장한다.

- 모드를 추가하거나 제거한 경우
- 모드 배열 순서를 바꾼 경우
- 모드 파일을 직접 수정한 경우
- 림월드 또는 FastLoader 버전이 바뀐 경우
- 텍스처가 바뀌었는데 게임에 예전 텍스처가 보이는 경우

## 요구 사항

- Harmony가 먼저 설치되고 FastLoader보다 먼저 로드되어야 한다.

## 무엇을 줄이는가

FastLoader는 시작 로딩 중 반복 비용이 큰 두 구간을 캐시한다.

| 대상 | 림월드 기본 처리 | FastLoader 처리 |
|---|---|---|
| XML | 매 실행마다 XML 파일을 읽고 patch와 resolve를 다시 수행 | 처리 완료된 resolved XML을 저장해 재사용 |
| 텍스처 | 매 실행마다 PNG/JPG/PSD를 읽고 디코딩, 압축, 등록 | 이미 로드된 텍스처 데이터를 `.texcache`로 저장해 재사용 |

## 동작 방식

### XML 캐시

림월드의 XML 로딩은 크게 세 단계로 볼 수 있다.

1. **XML 파싱**  
   각 모드의 `Defs/*.xml`을 읽고 하나의 XML 문서로 모은다.
2. **Patch 적용**  
   각 모드의 `Patches/*.xml`에 있는 XPath patch를 적용한다.
3. **Resolve / Def 생성**  
   XML inheritance를 해석하고, 최종 XML 노드에서 `Def` 객체를 만든다.

FastLoader는 patch와 inheritance 해석이 끝난 XML을 `resolved_defs.xml`로 저장한다. 다음 실행에서는 XML 파일 읽기, 문서 통합, XPath patch 적용, inheritance 해석을 대부분 건너뛰고, 저장된 XML에서 `Def` 객체를 만든다.

즉 최종 `Def` 객체를 통째로 저장하는 방식이 아니라, 림월드가 `Def`를 만들기 직전의 XML 상태를 저장하는 방식이다.

### 텍스처 캐시

림월드의 모드 텍스처 로딩은 보통 다음 과정을 거친다.

1. 모드별 `Textures/` 폴더에서 이미지 파일을 찾는다.
2. PNG/JPG/PSD 파일을 읽고 `Texture2D`로 디코딩한다.
3. Unity 런타임에서 텍스처를 압축하고 `Apply`한 뒤 등록한다.

FastLoader는 한 번 로드된 텍스처를 다시 읽어 raw texture data로 저장한다. 이후 실행에서는 개별 이미지 파일을 다시 열고 디코딩하는 대신, 모드별 `.texcache` 파일에서 raw data를 읽어 `Texture2D.LoadRawTextureData`로 바로 복원한다.

이 방식으로 줄어드는 비용은 다음과 같다.

- 수천 개 파일을 개별 open/read 하는 비용
- PNG/JPG/PSD 디코딩 비용
- 런타임 텍스처 압축 비용

현재 텍스처 캐시는 워크샵/로컬 모드 기준으로 모드별 파일에 저장된다. Core와 공식 DLC 리소스는 이미 Unity asset/AssetBundle 형태라 텍스처 캐시 대상에서 제외된다. 여러 모드를 하나로 묶는 그룹 캐시도 지원할 수 있도록 파일 포맷이 분리되어 있다.

## 캐시 파일 구조

캐시는 림월드 설정 폴더 아래에 생성된다.

```text
%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Config\FastLoader\
├── Cache\
│   ├── manifest.xml
│   └── resolved_defs.xml
├── TextureCache\
│   ├── mod_{packageid}.texcache
│   ├── group_{groupid}.texcache
│   └── groups.xml
└── AudioCache\                  # 예정
    └── mod_{packageid}.audiocache
```

### XML 캐시

| 파일 | 설명 |
|---|---|
| `Cache/manifest.xml` | 게임 버전, FastLoader 버전, 모드 구성 해시, 캐시 항목 메타데이터 |
| `Cache/resolved_defs.xml` | patch와 inheritance가 반영된 최종 XML 문서 |

### 텍스처 캐시

| 파일 | 설명 |
|---|---|
| `TextureCache/mod_{packageid}.texcache` | 모드 하나의 텍스처 raw data |
| `TextureCache/group_{groupid}.texcache` | 여러 모드를 묶은 텍스처 raw data |
| `TextureCache/groups.xml` | 그룹 캐시 설정 |

`.texcache` 파일에는 텍스처 경로, 이름, 크기, 포맷, 필터 설정, raw texture byte 배열이 들어간다.

#### `manifest.xml`

XML 캐시의 검증 정보와 `resolved_defs.xml` 안의 Def 출처 정보를 저장한다.

```xml
<FastLoaderCache>
  <formatVersion>1</formatVersion>
  <fastLoaderVersion>0.2.2</fastLoaderVersion>
  <gameVersion>...</gameVersion>
  <inputHash>...</inputHash>
  <createdUtc>...</createdUtc>
  <entries>
    <entry packageId="mod.package.id" sourceName="Defs/File.xml" />
  </entries>
</FastLoaderCache>
```

`inputHash`는 현재 림월드 버전, 활성 모드 수, 모드 배열 순서, `packageId`, `packageIdPlayerFacing`을 기준으로 만든다. 모드 폴더를 재귀 순회해서 파일 내용을 검증하지는 않는다.

#### `resolved_defs.xml`

Patch와 XML inheritance가 적용된 뒤의 최종 `Defs` 문서를 저장한다.

```xml
<Defs>
  <ThingDef>...</ThingDef>
  <RecipeDef>...</RecipeDef>
</Defs>
```

#### `.texcache`

텍스처 캐시는 `BinaryWriter` 기반 바이너리 파일이다. 실제 C# `struct`로 저장하는 것은 아니지만, 파일 레이아웃은 다음 구조와 같다.

```csharp
struct TextureCacheHeader
{
    byte[4] Magic;          // "FLTX"
    int FormatVersion;      // 현재 1
    byte[64] CacheHash;     // UTF-8 고정 길이 문자열
    int TextureCount;
    long CreatedUtcTicks;
}

struct RawTextureEntryMeta
{
    string InternalPath;
    string Name;
    int Width;
    int Height;
    int TextureFormat;      // UnityEngine.TextureFormat
    int MipmapCount;
    int FilterMode;         // UnityEngine.FilterMode
    int AnisoLevel;
    int RawDataLength;
}

byte[] RawData[TextureCount];
```

저장 순서는 헤더, 텍스처 메타데이터 배열, raw texture byte 배열 순서다. 읽을 때는 먼저 `Magic`, `FormatVersion`, `CacheHash`를 확인하고, 맞는 경우에만 raw texture data를 읽어 `Texture2D.LoadRawTextureData`로 복원한다.

`CacheHash`는 FastLoader 버전, 림월드 버전, 현재 모드 배열 해시, 모드 `packageId` 또는 그룹 ID를 기준으로 만든다.

#### `groups.xml`

여러 모드의 텍스처를 하나의 `group_{groupid}.texcache`로 묶기 위한 설정 파일이다.

```xml
<TextureCacheGroups>
  <group id="group-id" name="Group Name">
    <mod>mod.package.id</mod>
  </group>
</TextureCacheGroups>
```

### 오디오 캐시

오디오 캐시는 아직 적용 전이다. 이후 구현 시 `AudioCache/` 아래에 모드별 오디오 캐시를 저장하는 구조를 사용할 예정이다.

## 모드 설정

위치:

```text
Options -> Mod Settings -> FastLoader
```

| 항목 | 설명 |
|---|---|
| `Enable FastLoader cache` | FastLoader 캐시 사용 여부 |
| `Build XML cache now` | 현재 로딩에서 확보한 resolved XML을 저장 |
| `Build texture cache now` | 현재 메모리에 로드된 모드 텍스처를 `.texcache`로 저장 |
| `Reset XML cache` | XML 캐시 삭제 |
| `Reset texture cache` | 텍스처 캐시 삭제 |
| `Reset all caches` | XML과 텍스처 캐시 전체 삭제 |

---

`solaris.fastloader` / RimWorld 1.6
