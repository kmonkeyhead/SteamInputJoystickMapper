# Steam Input Joystick Mapper

DirectInput/HID 비행 조이스틱의 축/버튼을 GUI에서 **게임별로** 매핑하고, 이를 Steam Input Xbox 패드 설정으로 변환해 적용하는 Windows 설정 도구 (C# / .NET 8 / WPF).

가상 컨트롤러 드라이버가 아닙니다. 앱은 Steam Input 설정 파일만 만들고, 실제 변환은 Steam이 합니다. 앱을 종료해도 설정은 유지됩니다.

![main](screenshots/main.png)

## 빌드 / 실행

```
dotnet build
dotnet test
dotnet run --project src/SteamJoystickMapper
```

게시(`dotnet publish -p:PublishProfile=FolderProfile` 또는 Visual Studio 게시)는 매번 이전 빌드 결과를 지우고 처음부터 빌드합니다. 게시 전에 앱을 닫아 주세요.

## 사용 방법

1. 조이스틱을 연결하고 앱을 실행합니다 (입력 장치는 자동 선택).
2. 게임을 고르고 **이 게임 매핑 만들기** → 출력마다 [감지]를 누르고 조이스틱을 움직입니다.
3. **[Steam에 적용]**: 조이스틱을 Steam 일반 컨트롤러로 등록하고 게임별 설정을 씁니다. Steam 설정 화면에서 따로 할 일은 없습니다.
4. **[Steam에서 비활성화]**: 일반 컨트롤러 등록을 지워 Steam에서 다시 조이스틱으로 보이게 합니다 (게임 매핑과 게임별 파일은 남김).

매핑 기능

- 축 → 스틱 축 (반전, 데드존, 외곽 데드존)
- 버튼 / POV → 버튼, D-Pad, LT/RT
- **축 한쪽 방향 → 버튼**: 예) 트위스트 왼쪽 → LB, 오른쪽 → RB, 데드존 % 를 넘게 꺾어야 눌림
- **쓰로틀 → LT/RT 아날로그**: [감지] 후 쓰로틀을 시작 위치 → 최대 위치로 움직여 멈추면 그 구간이 범위 (멈춘 쪽이 최대). 시작~끝 % 숫자로도 수정 가능
- **한 출력에 입력 여러 개** ([+] 버튼): 예) 쓰로틀과 버튼 둘 다 LT

## Steam Input 설정 구조 (실제 파일 분석 결과)

Steam은 일반(DirectInput/HID) 컨트롤러를 **2단계**로 처리합니다.

| 단계 | 파일 | 범위 | 내용 |
|---|---|---|---|
| (1) 장치 설정 (레이아웃) | `<Steam>/config/config.vdf` → `SDL_GamepadBind` | 장치당 1개, 모든 게임 공유 | 물리 입력 → 게임패드 자리 (`leftx:a0`, `b:b0`, `dpup:h0.1`, 반전 `~`) |
| (2) 게임별 설정 | `<Steam>/steamapps/common/Steam Controller Configs/<계정>/config/<AppID>/controller_generic.vdf` | 게임별 | 게임패드 자리 → Xbox 출력 (`xinput_button B`, `output_joystick`, `output_trigger`, 데드존, 트리거 범위) |
| 선택 | 같은 폴더의 `configset_controller_generic.vdf` | | 게임별로 어떤 설정을 쓸지 (`autosave`/`workshop`) |
| Steam Input 사용 | `<Steam>/userdata/<계정>/config/localconfig.vdf` → `apps/<AppID>/UseSteamControllerConfig` | 게임별 | 0 사용 안 함 / 1 기본 / 2 사용 |

- 장치 설정은 Steam에 장치당 하나뿐이므로 **사용자가 만들지 않습니다.** 앱이 모든 게임 매핑에 쓰인 입력을 모아 자동으로 만들고, 각 게임 매핑은 (2)에서 그 자리를 원하는 출력으로 다시 연결합니다.
- 장치 설정이 등록되면 Steam은 이 장치의 게임별 설정을 `controller_generic` 이름으로 읽고 씁니다.
- 축 반전은 장치 설정에만 있으므로 **같은 축의 반전은 모든 게임에서 같아야** 합니다 (다르면 검증 오류).
- 모든 게임을 합쳐 쓰는 입력이 장치 설정 자리 수(스틱 축 4, 트리거 2, 버튼 14)를 넘으면 검증 오류.
- 게임 매핑이 있는 게임은 적용 시 게임 속성의 Steam Input을 "사용"으로 바꿉니다. `localconfig.vdf`는 통째로 다시 쓰지 않고 해당 값만 텍스트로 고칩니다.
- 쓰로틀 → LT/RT: 쓰로틀 축 전체를 RT 자리(그대로)와 LT 자리(반전)에 두고, 게임별 트리거 범위 시작/끝(`deadzone_inner_radius`/`deadzone_outer_radius`)으로 구간을 정합니다.
- 트위스트 → LB/RB: 트위스트를 스틱 자리에 두고, 게임별 설정에서 그 스틱을 방향 패드 모드(`mode dpad`, `deadzone`)로 바꿉니다.
- 게임 매핑을 삭제한 게임은 앱이 만든 파일을 백업 폴더로 옮기고 configset 항목을 지웁니다.

생성 시 Steam이 만든 기존 파일(없으면 Steam 설치 폴더의 `controller_generic_gamepad_joystick.vdf` 템플릿)을 파싱해 필요한 바인딩만 바꿉니다. 설정 키는 Steam이 만든 파일에서 확인된 것만 사용합니다.

## 적용 절차

1. 프로필 검증 (필수 필드, AppID, 출력 충돌, 반전 불일치, 장치 설정 자리 수, 미지원 기능)
2. Steam 실행 중이면 종료 안내 → `steam.exe -shutdown` 후 종료 대기
3. Steam 종료 후 설정을 **다시 생성/검증** (Steam이 종료 시 config.vdf를 다시 저장하므로)
4. 자동 백업 (원본 경로, 수정 시간, SHA256)
5. 임시 파일에 쓰고 교체 → 다시 읽어 검증. 실패하면 백업으로 자동 복원

쓰기 가능한 경로는 해당 계정의 `Steam Controller Configs/.../config/` 아래 `.vdf`, `config/config.vdf`, 해당 계정의 `localconfig.vdf`뿐입니다 (코드에서 강제). 게임 파일, Steam 실행 파일, Steam 클라우드 파일은 건드리지 않습니다.

## 앱 데이터

`%AppData%\SteamJoystickMapper\`

- `profile.json` — 유일한 프로필 (장치 + 게임별 매핑)
- `settings.json` — 마지막으로 선택한 게임/장치
- `Backup\yyyy-MM-dd_HHmmss\` — 백업 파일 + `backup.json`
- `Logs\yyyyMMdd.log` — 로그 (Steam 계정 ID/이름은 `<user>`로 마스킹)

Claude 데스크톱 앱(MSIX) 안에서 실행하면 AppData 쓰기가 패키지 폴더로 리디렉션됩니다. 앱은 탐색기에서 실행하세요.

## 알려진 제약

- 장치 설정은 Steam이 VID/PID 단위로 저장하므로, 같은 제품을 여러 개 연결하면 설정을 공유합니다.
- 게임별 스틱 반전, 오른쪽 스틱 소스 → 왼쪽 스틱 출력, Guide 버튼, 감도/커브, Toggle/Turbo는 지원하지 않습니다.
- 매핑이 없는 게임은 자동 생성된 장치 설정 그대로(자리 = 같은 Xbox 입력) 동작합니다.

## 프로젝트 구조

```
src/SteamJoystickMapper/
  UI/        MainWindow, MappingEditorWindow(입력 감지 + 모니터), BackupHistoryWindow, Dialogs
  Devices/   DeviceScanner, InputReader, InputDetector (Vortice.DirectInput)
  Steam/     SteamLocator, SteamUserDetector, SteamGameScanner, SteamInputConfigFinder, SteamInputSetting,
             SteamLayoutPlanner, SteamConfigGenerator, SteamConfigValidator, SteamApplyService,
             SteamProcess, Vdf/(Parser, Writer, Node), Sdl/(SdlMapping, SdlElements)
  Mapping/   MappingProfile, MappingBinding, PhysicalInput, XboxOutput, ProfileStore, Presets
  Backup/    BackupManager
  Config/    AppSettings, AppPaths
  Logging/   AppLog
tests/SteamJoystickMapper.Tests/   VDF/SDL/플래너/생성기/검증기/적용/백업 테스트
```
