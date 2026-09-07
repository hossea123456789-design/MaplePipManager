# Changelog

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
