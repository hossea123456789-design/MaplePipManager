# Maple PiP Manager v0.9.0-beta

첫 공개 Beta 버전입니다.

## 주요 기능
- MapleStory 화면 원하는 영역을 크롭하여 PiP로 표시
- 여러 크롭 추가 / 이동 / 크기 조절 / 회전
- 메인 PiP와 분리 PiP
- Shift 다중 선택 및 정렬
- 프리셋 6개
- 메이플 기준 상대 위치 고정
- PIP 클릭 무시
- PIP별 크롭 투명도
- PIP별 배경 투명도
- 크롭별 색상 테두리 / 테두리 투명도
- 크롭 스타일 복사/붙여넣기
- FilterKeys 상태 PiP

## 실험 기능(Beta)
### 준극딜 / 극딜 / 오리진 쿨타임 감지
- 스킬 크롭별 READY / 사용 직후 상태 학습
- 역할별 스킬키 등록 및 화면 변화 교차 검증
- 역할별 감지 지연 보정
- 임박 알림 기준 설정

이 기능은 아직 실험적이며 상황에 따라 오탐/미탐이 발생할 수 있습니다.

## 시스템 요구사항
- Windows 10 2004(빌드 19041) 이상 x64 / Windows 11 x64
- Release ZIP은 .NET 8 **self-contained** 빌드로 배포합니다.
- 일반 사용자는 .NET Runtime / .NET Desktop Runtime / .NET SDK를 별도로 설치할 필요가 없습니다.
- Release ZIP을 전체 압축 해제한 뒤 `CropPipViewer.exe`를 실행합니다.

## 설정 위치
`%APPDATA%\CropPipViewer`

## 알려진 문제
극딜 감지는 쿨 종료 직후 매우 빠른 재사용, 보스방 이동에 의한 강제 쿨 초기화 등 특수 상태에서 추가 검증이 필요합니다.

## 배포 패키지
- Windows x64 self-contained **single EXE** 배포
- 일반 사용자는 .NET Runtime / Desktop Runtime / SDK 별도 설치 불필요
- 새 Maple PiP Manager 앱 아이콘 적용
- 압축 해제 후 최상단의 `CropPipViewer.exe` 실행
