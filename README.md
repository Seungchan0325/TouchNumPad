# TouchNumPad

**TouchNumPad**는 Windows 11 랩탑의 정밀 터치패드(Precision Touchpad)를 화면상의 넘패드(Numpad)와 1:1로 매핑하여 사용할 수 있게 해주는 경량 유틸리티입니다.

---

## 주요 기능

- **터치패드 → 넘패드 1:1 매핑**
  - 넘패드 창이 열려 있는 동안 터치패드 전체 영역이 4×5 넘패드(`Backspace`, `÷`, `×`, `−`, `7~9`, `+`, `4~6`, `1~3`, `Enter`, `0`, `.`)로 작동합니다.
  - 터치패드 위에 손가락을 올려놓고 움직이면 넘패드 창 위에 강조색 원으로 현재 손가락 위치와 선택된 키를 실시간으로 표시합니다.
  - 제자리 탭(또는 터치패드 물리 클릭) 시 현재 위치의 키가 활성 창에 즉시 입력됩니다.
- **직관적인 넘패드 ↔ 마우스 전환**
  - 넘패드 창이 열리면 마우스 커서가 자동으로 숨겨지고 넘패드 모드로 시작합니다.
  - 터치패드에서 손가락을 크게 드래그하여 **넘패드 창 끝(가장자리)에 도달하면** 해당 위치에서 마우스 커서가 나타나며 일반 터치패드(마우스)로 사용할 수 있습니다.
  - 다시 넘패드 창(설정·닫기 버튼 제외)을 클릭하면 마우스가 숨겨지고 넘패드 모드로 전환됩니다.
- **빠른 열기 / 닫기**
  - **터치패드 상단 제스처**:
    - 상단을 **왼쪽 끝에서 오른쪽 끝으로** 쓸어내리면 넘패드 창이 열립니다.
    - 상단을 **오른쪽 끝에서 왼쪽 끝으로** 쓸어내리면 넘패드 창이 닫힙니다.
  - **전역 단축키**: 기본 `Shift + Alt + N` (설정에서 PowerToys 스타일 단축키 레코더로 변경 가능)
- **입력 위치(캐럿) 근처 자동 배치**
  - 사용자가 텍스트를 입력 중일 때 넘패드를 호출하면, Win32 캐럿 및 UI Automation을 통해 현재 입력 커서(캐럿) 또는 입력칸 근처에 창을 자동으로 띄웁니다.
- **Windows 11 기본 앱 디자인**
  - 넘패드 팝업창과 독립된 설정창 모두 Windows 11 **Mica** 재질과 둥근 모서리가 적용되어 있습니다.
  - 설정창에서 **앱 테마**(시스템 설정 사용 / 밝게 / 어둡게), **창 불투명도**(25% ~ 100%), **활성화 단축키**를 설정할 수 있습니다.
  - 시스템 트레이(작업 표시줄 알림 영역)에서 상시 실행되며 빠르게 열기/설정/종료에 접근할 수 있습니다.

---

## 사용 방법

| 동작 | 방법 |
| :--- | :--- |
| **넘패드 열기** | 터치패드 상단 `좌 → 우` 쓸어내리기 또는 `Shift + Alt + N` |
| **넘패드 닫기** | 터치패드 상단 `우 → 좌` 쓸어내리기, `Shift + Alt + N`, 또는 닫기(`✕`) 버튼 |
| **숫자 / 연산자 입력** | 넘패드 모드에서 터치패드 위에 표시된 위치를 확인 후 탭(또는 물리 클릭) |
| **일반 터치패드(마우스)로 전환** | 넘패드 창 끝까지 크게 드래그하여 가장자리에 도달 |
| **다시 넘패드 모드로 복귀** | 마우스로 넘패드 창(설정·닫기 버튼 제외) 클릭 |
| **창 위치 이동** | 마우스 모드에서 넘패드 상단 헤더 바 드래그 |

---

## 요구 사항 및 기술 스택

- **OS**: Windows 10 (1809+) / Windows 11 (Windows Precision Touchpad 탑재 노트북)
- **Framework**: .NET 10.0 (`net10.0-windows10.0.22621.0`)
- **UI**: WinUI 3 (Windows App SDK 1.6)
- **System Interop**: Win32 Raw Input (`WM_INPUT`), HID Parser (`hid.dll`), Low-Level Hooks (`WH_MOUSE_LL`, `WH_KEYBOARD_LL`), UI Automation (`IUIAutomation`)

---

## 빌드 및 실행

```powershell
# 빌드 (Release)
dotnet build -c Release

# 실행 (Release)
dotnet run -c Release
```

설정은 `%LOCALAPPDATA%\TouchNumPad\settings.json`에 자동으로 저장됩니다.

---

## 프로젝트 구조

```text
TouchNumPad/
├── App.xaml / App.xaml.cs               # 단일 인스턴스 관리 및 앱 진입점
├── MainWindow.xaml / MainWindow.xaml.cs # 넘패드 팝업창 (Mica, 터치 오버레이, 모드 전환)
├── SettingsWindow.xaml / .cs            # Windows 11 스타일 설정창 및 단축키 레코더
├── Interop/
│   └── NativeMethods.cs                 # Win32 / HID / DWM / UIA P/Invoke 선언
├── Models/
│   └── AppSettings.cs                   # 앱 설정 직렬화 및 저장
└── Services/
    ├── CaretPositionService.cs          # 활성 입력 캐럿 / 입력칸 좌표 감지
    ├── CursorVisibilityService.cs       # 시스템 마우스 커서 숨김 및 복원
    ├── KeyboardInjector.cs              # NumLock 무관 키보드 입력 주입
    ├── NumpadKeyMap.cs                  # 4x5 넘패드 레이아웃 및 좌표 히트 테스트
    ├── TouchpadInputService.cs          # 정밀 터치패드 Raw Input HID 파싱 및 제스처 처리
    └── TrayIconService.cs               # 시스템 트레이 아이콘 및 컨텍스트 메뉴
```
