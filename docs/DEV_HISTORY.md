[v33c]
- 크롭 이미지 투명도를 PIP별로 독립 저장/적용하도록 변경했습니다.
- 메인 PIP는 `__main__`, 분리 PIP는 group id를 키로 사용하는 `PipCropOpacityByKey`를 사용합니다.
- Shift 다중 선택 시 선택된 PIP들만 일괄 적용할 수 있고 전체 적용 기능도 유지합니다.
- 프리셋/재실행 후 PIP별 크롭 투명도가 복원되도록 저장/clone 경로를 반영했습니다.

[공개 패키징 v0.9.0-beta]
- 일반 사용자용 Release는 `win-x64` .NET 8 self-contained 방식으로 생성합니다.
- 일반 사용자는 .NET Runtime/Desktop Runtime/SDK를 별도로 설치하지 않습니다.
- `BUILD_PUBLIC_RELEASE.bat`가 `coreclr.dll`, `hostfxr.dll`, `PresentationFramework.dll` 존재를 확인한 뒤 ZIP을 생성합니다.
- 최종 사용자는 Release ZIP을 전체 압축 해제하고 `CropPipViewer.exe`를 실행합니다.

[v33b]
- Fixed crop border color/opacity persistence across preset save, restart, and preset load.
- CloneCrop now preserves BorderColorHex and BorderOpacity.
- Added startup compatibility recovery for v31-v33a settings when root Crops still contain border style data.
- Keeps v33a hybrid burst input verification/build fixes.

[v33a]
- BUILD FIX: missing _burstInputTimer field restored (10ms DispatcherTimer).
- BUILD/RUN version labels synchronized to v33a.

[v33]
Hybrid burst verification: configured skill-key input + visual classifier confirmation.
- 준극딜/극딜/오리진별 스킬키를 등록할 수 있습니다.
- 등록한 3개 키만 GetAsyncKeyState로 폴링하며, 선택된 MapleStory 프로세스가 포그라운드일 때만 입력 후보를 만듭니다.
- 키 입력만으로 쿨타이머를 시작하지 않습니다. 키 입력 직후 학습된 COOLDOWN 화면 전환이 확인될 때만 사용으로 확정합니다.
- 자연 쿨 종료 직후 READY 프레임을 못 보고 바로 재사용해도, 입력 시각 + 근접 만료 + 화면 변화량으로 재사용을 검증합니다.
- 보스방 강제 초기화로 내부 타이머가 남아 있어도 직전 화면이 READY로 확인된 상태에서 등록키 입력 후 COOLDOWN 전환이 나오면 기존 타이머를 폐기하고 입력 시각부터 새 쿨을 시작합니다.
- 극딜 감지 샘플링은 평상시 33ms, 종료 근처/전환 구간 16ms로 강화했습니다.
- 사용 확정 타이머 시작시각은 AI 확정 시각이 아니라 실제 등록키 입력 감지 시각으로 소급합니다.

[v32]
Temporal burst verification: fast expiry sampling, instant-recast detection, boss-room cooldown reset detection.

[v30d]
- 내부 쿨타이머가 0초가 되는 즉시 감지 PIP를 `준비`로 전환합니다. 기존 `복귀확인` 대기 상태는 제거했습니다.
- 표시 READY와 다음 사용 감지 재무장을 분리했습니다. 0초에서는 즉시 READY를 표시하되, 이전 쿨다운 꼬리를 새 사용으로 오인하지 않도록 학습된 READY 화면 1프레임을 확인한 뒤 다음 READY→COOLDOWN 감지를 재무장합니다.
- 종료 표시 지연은 제거하면서 중복/오탐 재감지를 막는 구조입니다.

[v30c]
- 준극딜/극딜/오리진 각각에 `보정(초)` 입력칸을 추가했습니다.
- 보정값은 감지 지연을 상쇄하기 위한 값이며, 해당 역할의 내부 남은 쿨타임에서 입력한 초만큼 추가 차감합니다. 예: 준극딜 감지가 실제보다 항상 약 2초 늦으면 `2` 입력.
- 역할별 보정값은 0~30초, 소수 둘째자리까지 저장 가능하며 프리셋별로 저장/불러옵니다.
- 보정값은 현재 진행 중인 내부 타이머에도 즉시 반영하며 감지 학습/상태를 초기화하지 않습니다.
- `PiP 표시/숨김`으로 일반 PiP를 숨길 때 극딜 감지 PiP도 같이 숨고, 다시 표시할 때 `감지 PIP 표시` 설정이 켜져 있으면 함께 복구됩니다.
- `필터키만 표시`에서도 극딜 감지 PiP를 같이 숨깁니다.

[v30b]
- 프리셋 해상도 매칭 기준을 자동 저장/위치고정 baseline에서 분리했습니다.
- 기준 해상도는 해당 프리셋에서 `현재 설정 저장`을 명시적으로 누른 최초 시점의 WGC SourceWidth/SourceHeight로만 기록되며, 이후 메이플 해상도 변경/대상 재선택/자동 저장/PIP 이동으로 덮어쓰지 않습니다.
- 과거 버전에서 자동으로 채워져 신뢰할 수 없는 해상도 값은 v30b에서 기준 미저장으로 취급합니다. 올바른 해상도로 연결한 뒤 `현재 설정 저장`을 한 번 누르면 새 기준이 생깁니다.
- 필요할 때만 `현재 연결 해상도로 기준 다시 저장` 버튼으로 기준값을 강제 갱신할 수 있습니다.
- 극딜 감지 PIP 임박 기준을 `임박 (초 미만)` 입력값으로 분리했습니다. 기본값 5초, 허용 범위 0~600초이며 내부 타이머가 있을 때만 적용됩니다.
- 감지 PIP 남은 시간은 60초 이상이면 `2m 5s`, 60초 미만이면 `42s` 형식으로 표시합니다.
- 감지 PIP에 `전체 투명도`와 `배경 투명도`를 분리했습니다. 배경은 0%에서 완전 투명하며 글자/상태 표시는 유지됩니다.
- 투명도/배경 투명도/임박 기준은 프리셋별로 저장됩니다.
- PIP 클릭 무시 ON 시 기존처럼 감지 PIP 테두리가 사라지고 클릭이 통과됩니다.

[v30a]
- 극딜 감지 PIP의 임박 기준을 역할과 무관하게 남은 시간 5초 미만으로 변경.
- 임박/반짝임은 확정된 내부 쿨타이머에서만 발생하며 이미지 분류 결과만으로는 발생하지 않음.

[v30]
- 극딜 감지 엔진 전면 교체: 자동 밝기/OCR/숫자 힌트 판정 제거.
- 각 스킬 크롭별로 READY(준비) / 사용 직후(COOLDOWN) 실제 화면을 3.2초씩 직접 학습하는 few-shot 분류기 사용.
- 12x12 다중 채널 특징 + shrinkage LDA 방식으로 두 상태를 분류하고, 3프레임 연속 시간축 검증 후 READY -> COOLDOWN 전환만 사용으로 확정.
- READY가 먼저 확정되지 않은 상태에서 COOLDOWN으로 보여도 내부 타이머를 시작하지 않음. 시작 시 이미 쿨중인 경우 남은 시간을 날조하지 않음.
- 임박은 이미지 판정이 아니라 확정된 내부 타이머에서만 계산.
- 학습 데이터는 프리셋/크롭별 저장. 크롭 크기나 메이플 해상도가 달라지면 재학습 요구.

학습 순서
1) 역할 크롭 지정
2) 스킬 사용 가능 상태에서 [준비 학습]
3) 스킬을 실제 사용한 직후 [사용 직후 학습]
4) 두 학습이 완료된 뒤 PIP에서 READY 상태 확인
5) 실제 스킬 사용 -> READY->COOLDOWN 전환 3프레임 확인 후 내부 쿨타이머 시작

[v29c]
- 극딜 감지 밝기/초기 쿨중 자동 판정 제거.
- 기준 준비 이미지가 학습되기 전에는 절대 쿨중/임박 타이머를 만들지 않음.
- 준비 기준 대비 이미지 차이 + 숫자/어두운 오버레이 보조 신호가 2회 연속일 때만 사용 감지.
- 기존 쿨중처럼 보이는 상태는 동기화 대기로만 표시해 오탐 방지.

[v29a]
- 극딜 감지 PIP가 이미 쿨타임이 돌고 있는 아이콘을 초기에 `쿨중`으로 표시하도록 보강했습니다.
- 이미 쿨중인 상태는 사용 시점을 알 수 없으므로 임의 타이머를 시작하지 않습니다. 밝음→어두움 변화가 실제로 감지된 경우에만 내부 타이머/임박 알림을 시작합니다.
- 오탐 방지를 위해 연속 쿨다운 프레임 5회 확인, 어두운 비율, 밝은 숫자/표식 비율, 채도 조건을 함께 봅니다.
- `PIP 클릭 무시` ON 시 극딜 감지 PIP도 클릭 통과 처리되고 테두리를 숨깁니다.
- 극딜 감지 PIP 하단의 쿨감 설명 텍스트를 제거해 전투 중 표시를 더 작게 정리했습니다.

CropPipViewer v29

- 프리셋 해상도 표시를 "현재 연결 해상도"와 "처음 저장된 기준 해상도"로 분리했습니다.
- 대상 창을 다시 선택하거나 메이플 해상도를 바꿔도 이미 저장된 프리셋 기준 해상도는 자동으로 덮어쓰지 않습니다.
- 기준 해상도가 비어 있는 프리셋만 현재 연결 해상도를 최초 기준값으로 기록합니다.
- 해상도 매칭 UI 문구를 `저장값`이 아니라 `기준 해상도`로 정리했습니다.

CropPipViewer v29

개요
- .NET 8 WPF / Windows Graphics Capture 기반 크롭 PIP 도구입니다.
- TargetFramework: net8.0-windows10.0.19041.0
- CsWinRTEnabled=false 유지

[v28a]
- `메이플 해상도 매칭` 영역을 카드형 UI로 정리했습니다.
- 현재 연결/현재 슬롯 저장값을 2줄 요약으로 표시합니다.
- 일치/불일치/미연결/저장값 없음 상태를 우측 배지로 표시합니다.
- P1~P6 저장 해상도는 2열 x 3행으로 줄바꿈해 표시합니다.
- 한 줄에 몰리던 프리셋별 저장값 표시를 제거해 기본 창 폭에서도 읽기 쉽게 했습니다.

[v28]
- PIP 탭 상단에 `메이플 해상도 매칭` 영역을 추가했습니다.
- 현재 WGC로 연결된 메이플 해상도와 현재 프리셋의 기준 해상도를 동시에 표시합니다.
- 6개 프리셋별 기준 해상도를 표시하고, 현재 연결 해상도와 일치하는 프리셋에는 `✓` 표시를 붙입니다.
- 기준 해상도가 비어 있는 프리셋만 현재 연결된 WGC SourceWidth/SourceHeight를 `TargetSourceWidth/TargetSourceHeight` 최초값으로 기록합니다.
- 이전 버전 저장값은 기존 PIP 위치 고정 baseline 해상도에서 가능한 경우 자동으로 읽어와 표시합니다.

[v27d]
- 메인 프로그램 UI 전체 레이아웃을 재정리했습니다.
  - 상단: 대상 창 / 프리셋
  - 좌측: 크롭 목록과 크롭 조작 버튼
  - 우측: 탭형 설정 패널(PIP / 성능 / 필터키)
- 기본 창 크기에서 옵션들이 아래로 밀려 보이지 않는 문제를 줄였습니다.
- 설정 영역은 각 탭 내부 ScrollViewer로 처리해 창 크기가 작아져도 접근 가능합니다.
- 중복 성격이 강한 하단 `저장` 버튼을 제거하고, 상단 프리셋 영역의 `현재 설정 저장`으로 저장 동선을 통일했습니다.
- 크롭 조작 버튼은 `크롭 추가`, `선택 삭제`, `로그 폴더`만 좌측 상단에 배치했습니다.
- 필터키 관련 옵션은 별도 `필터키` 탭으로 묶어 UI 밀림을 방지했습니다.
- PIP 투명도/배경/확대/위치 고정 옵션은 `PIP` 탭으로 묶었습니다.
- 갱신 속도/FPS 표시 옵션은 `성능` 탭으로 묶었습니다.

[v27b]
- 필터키 알약 PIP에 `위치 잠금` 체크박스를 추가했습니다.
- `필터키만 표시` 버튼을 추가했습니다. 일반 PIP는 모두 숨기고 필터키 알약 PIP 버튼만 표시합니다.
- 필터키 알약의 위치 잠금 상태도 6개 프리셋별로 저장됩니다.

[v27a]
- FilterKeys pill was reduced to 64x22 and displays OFF/대기/ON/오류.
- Pill dragging now captures the actual pill border, so drag movement should work reliably.
- When "PIP 위치 고정" is enabled, the FilterKeys pill follows the selected MapleStory client area using target-relative offsets.
- RUN.BAT was added. RUN_RELEASE.BAT/RUN_DEBUG.BAT target net8.0-windows10.0.19041.0 to match the csproj.

[v27]
- 필터키 상태를 작은 알약 모양 PIP 버튼으로 띄우는 `PIP 버튼` 체크박스를 추가했습니다.
- 알약 버튼 클릭 시 메인 프로그램의 `사용` 체크박스와 동기화되어 필터키 사용 ON/OFF가 전환됩니다.
- 알약 버튼은 드래그로 위치 이동 가능하며, 위치와 표시 여부는 프리셋별로 저장됩니다.

[v26]
- 겹친 PIP에서 이미 선택된 PIP를 움직임 없이 클릭하면 같은 위치에 겹쳐 있는 다른 PIP로 선택을 순환합니다.
- 크롭 목록에 미리보기 썸네일 열을 추가했습니다.
- 해상도별 자동 보정 기능은 검토했지만 이번 버전에는 넣지 않았습니다.

[v25]
- 필터키 ON/OFF 상태를 배지로 표시하도록 개선했습니다.
- 필터키 적용 루프 최적화로 반복 API 호출에 의한 렉을 줄였습니다.
- SystemParametersInfo 적용 시 Windows 프로필 저장 플래그를 제거하고 런타임 브로드캐스트만 사용하도록 변경했습니다.

빌드/실행
1. BUILD.BAT 실행
2. 빌드 성공 후 RUN.BAT 실행
3. 대상 창에서 MapleStory.exe 창 선택
4. 크롭 추가 후 PIP 표시

주의
- Windows SDK UAP Platform.xml 오류가 나는 환경을 고려해 TargetFramework는 net8.0-windows10.0.19041.0으로 낮춰져 있고 CsWinRTEnabled=false가 유지됩니다.
- 현재 공개 소스의 내부 기능 기준은 v33c입니다. 공개 버전 표기는 v0.9.0-beta입니다.


[v27d]
- 필터키 알약 PIP가 PIP 위치 고정 상태에서 메이플 창 이동을 따라가도록 보강했습니다.
- 필터키 알약 PIP가 뒤로 밀리지 않도록 TopMost를 주기적으로 재적용합니다.


[v27e]
- 필터키 알약 PIP 위치 고정 안정화: Ctrl+Enter/클라이언트 크기 변경 시에도 현재 메이플 클라이언트 영역 밖으로 사라지지 않도록 보정.
- 필터키 알약 TopMost 재적용 강화.
- 알약 위치 잠금 상태에서도 클릭 시 ON/OFF 토글 유지.
- 체크박스 Content 동적 변경 제거 및 UI 줄바뀜 완화.
