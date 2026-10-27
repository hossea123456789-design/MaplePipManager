# Changelog

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
