# Changelog

## v0.9.6-beta

### 수정
- Shift로 여러 PIP를 선택한 뒤 빠르게 드래그할 때 일부 PIP가 이동을 따라오지 못해 상대 위치와 정렬이 점점 틀어지는 문제 수정
- 다중 PIP 드래그 방식을 프레임별 누적 이동에서 드래그 시작 좌표 기준 절대 이동으로 변경
  - 드래그 시작 시 선택된 모든 PIP의 좌표를 한 번 스냅샷
  - 이후 각 PIP 위치를 `시작 좌표 + 전체 마우스 이동량`으로 계산
  - MouseMove 이벤트가 일부 누락되거나 창별 위치 이벤트 처리 속도가 달라도 PIP 간 상대 간격 유지
- 다중 드래그 중 메이플 창 기준 위치 고정 보정을 일시 중단하고, 드래그 종료 시 최종 위치 기준으로 오프셋을 한 번만 재계산
- 드래그 중 프레임별 설정 저장 제거, 종료 시 최종 배치만 1회 저장
- 마우스 캡처가 비정상적으로 끊겨도 그룹 드래그 상태가 정상 해제되도록 보강
- 크롭 표의 셀 변경 때마다 모든 PIP를 재생성하던 불필요한 경로 제거
  - 재생성 직후 Image Source가 비어 특정 크롭이 간헐적으로 안 보일 수 있던 타이밍 문제 수정
- 크롭 UI 재구성 시 마지막 정상 WGC 프레임을 즉시 재사용하고 다음 프레임을 갱신하도록 보강
- 소속 그룹 값은 남아 있는데 해당 분리 PIP 창이 누락된 orphan 상태를 감지하면 자동으로 PIP를 재생성하도록 self-heal 추가
- 메인 크롭 목록 선택 시 실제 PIP 안의 해당 크롭에 네온 위치 표시 FX 추가
  - 민트 네온 테두리의 밝기/두께 왕복 애니메이션
  - Shift 다중 선택 시 여러 크롭 동시 강조
  - 행 선택 해제 또는 메인 프로그램 포커스 해제 시 FX 자동 해제


## v0.9.5-beta

### 추가
- Maple PiP Manager 메인 창의 마지막 위치 저장/복원
  - 정상 종료 시 마지막 창 좌표를 전역 설정에 저장
  - 다음 실행 시 저장된 위치에서 메인 창 시작
  - 프리셋과 분리하여 P1~P6 전환과 무관하게 유지
  - 최대화/최소화 상태에서는 RestoreBounds 기준 정상 위치 저장
  - 모니터 구성/해상도 변경으로 저장 위치가 현재 가상 데스크톱 밖이 된 경우에는 해당 위치 복원을 건너뛰어 창이 화면 밖에 뜨는 문제 방지


## v0.9.4-beta

### 수정
- 대상 창 콤보박스에 Discord, 카카오톡, Crop PIP, FilterKeys Pill 등 관계없는 창이 노출되던 문제 수정
- 대상 창 목록을 MapleStory 후보로만 제한
- 동일 MapleStory 프로세스가 여러 top-level HWND를 노출해 목록에 중복 표시되던 문제 수정
  - PID당 대표 창 1개만 표시
  - Process.MainWindowHandle → 정확한 MapleStory 제목 → 가장 큰 클라이언트 영역 순으로 대표 창 우선 선택
- Maple PiP Manager 자신의 PIP/알약/보조 창은 대상 목록에서 완전히 제외
- 여러 MapleStory 클라이언트를 동시에 실행한 경우에는 서로 다른 PID별로 1개씩 표시


## v0.9.3-beta

### 긴급 수정
- 일부 환경에서 대상 창 선택 시 `GraphicsCaptureItemInterop.CreateForWindow`가 `System.ArgumentException (Value does not fall within the expected range)`으로 실패해 WGC 연결이 되지 않는 문제 수정
- `IGraphicsCaptureItemInterop::CreateForWindow`를 실제 COM ABI(HRESULT + out pointer)에 맞게 수정
- 대상 연결 직전에 HWND 유효성과 PID를 다시 확인하고, 오래된 창 핸들이면 현재 MapleStory 창 핸들로 재탐색
- 연결 실패 시 창 목록을 자동 새로고침하고 재선택할 수 있도록 오류 처리 보강


## v0.9.2-beta

### 수정
- Windows 시작 프로그램/자동 실행 경로에서 FilterKeys 알약 PIP가 비정상적으로 커질 수 있는 문제 수정
  - 메인 창이 Loaded 된 뒤 알약 PIP 생성
  - 알약 PIP 크기를 60×20 DIP로 강제 고정
- 프리셋 전환 중 일부 크롭이 사라지거나 위치/크기가 뒤틀릴 수 있는 경합 조건 수정
  - 프리셋 복원 중 이벤트 기반 자동 저장 차단
  - PIP 창 위치/크기 복원을 사용자 리사이즈와 분리
  - 전환 중간 상태가 새 프리셋의 PIP placement를 덮어쓰지 않도록 원자적 복원 처리
- 손상/레거시 프리셋 방어 로직 추가
  - 잘못된 표시 크기, 중복 Crop ID, 비정상 PIP placement 값을 로드 시 정규화

### 배포
- Windows GitHub Actions 빌드/패키징 검증 및 VERSION 기반 자동 pre-release 파이프라인 추가


## v0.9.1-beta
내부 개발 기준: v33c 기반 + 공개 피드백 반영

### 추가
- `메이플 활성 시만 PIP 표시` 옵션
  - 선택한 MapleStory 프로세스 또는 Maple PiP Manager가 활성화되어 있을 때만 PIP 표시
  - Chrome/Discord/Explorer 등 다른 앱으로 전환하면 메인/분리 PIP, 극딜 모니터, 필터키 알약 PIP 자동 숨김
  - 다시 메이플/매니저로 돌아오면 자동 숨김했던 창만 복원
- `업데이트 확인` 기능
  - GitHub Release의 새 버전을 앱에서 확인
  - 새 버전이 있으면 상단 버튼에 버전 표시
  - 승인 시 최신 win-x64 ZIP 자동 다운로드
  - SHA-256 체크섬이 제공되면 검증
  - 자동 압축 해제 → 현재 프로그램 종료 → 파일 교체 → 재실행

### 변경
- 공개 배포 버전을 `v0.9.1-beta`로 갱신
- 공개 릴리스 빌드 스크립트가 루트 `VERSION` 파일을 읽어 ZIP 파일명과 Assembly InformationalVersion에 자동 반영하도록 변경

### 제거
- `창 크기 연동` 기능 제거
  - 프리셋 전환 중 PIP 창 크기 복원이 실제 사용자 리사이즈로 처리되어 크롭 `표시 W/H`가 누적 변형될 수 있는 문제가 확인됨
  - 기존 설정 파일의 해당 값은 호환을 위해 읽을 수 있으나 앱에서 강제로 OFF 처리하고 UI에서는 숨김

## v0.9.0-beta
내부 개발 버전: v33c

### 주요 기능
- Windows Graphics Capture 기반 크롭 PiP
- 메인/분리 PiP, 다중 선택, 분리/병합
- 프리셋 6개 및 해상도 기준 표시
- PIP별 크롭/배경 투명도
- 크롭 테두리 색상/투명도 및 스타일 복사/붙여넣기
- PIP 위치 고정 / 클릭 무시
- FilterKeys 상태 PiP
- 극딜/준극딜/오리진 감지 Beta
- 역할별 감지 지연 보정 및 임박 기준 설정

### 주의
극딜 감지는 실험 기능으로 분류합니다. 빠른 재사용/보스방 강제 초기화 등 특수 상황은 추가 검증이 필요합니다.

### Packaging refresh
- End-user win-x64 release is now self-contained single-file.
- Extracted top level contains only `CropPipViewer.exe` plus the `docs/` folder.
- End users do not need to install the .NET Runtime or SDK.
