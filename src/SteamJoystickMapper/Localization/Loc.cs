using System.ComponentModel;
using System.Globalization;

namespace SteamJoystickMapper.Localization;

/// <summary>
/// 한국어/영어 UI 문구. 처음에는 시스템 언어로 정하고(한국어가 아니면 영어), 사용자가 고르면 앱 설정에 기억한다.
/// XAML은 <c>{Binding [키], Source={x:Static loc:Loc.Instance}}</c>로, 코드는 <see cref="T"/>로 쓴다.
/// 언어를 바꾸면 바인딩이 즉시 갱신되고 <see cref="Changed"/>로 코드 쪽 문구도 다시 그린다.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();
    public static bool IsKorean { get; private set; }
    public static event Action? Changed;
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>저장된 언어("ko"/"en")가 없으면 시스템 UI 언어로 정한다.</summary>
    public static void Init(string? saved) =>
        IsKorean = saved switch
        {
            "ko" => true,
            "en" => false,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko",
        };

    public static string Code => IsKorean ? "ko" : "en";

    public static void SetLanguage(bool korean)
    {
        if (IsKorean == korean) return;
        IsKorean = korean;
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs("Item[]"));
        Changed?.Invoke();
    }

    public static string T(string ko, string en) => IsKorean ? ko : en;

    public string this[string key] => Texts.TryGetValue(key, out var t) ? (IsKorean ? t.Ko : t.En) : key;

    private static readonly Dictionary<string, (string Ko, string En)> Texts = new()
    {
        // 공통 / 메인 창
        ["donate"] = ("후원하기", "Donate"),
        ["main.releaseInfo"] = ("버전 1.0 · 게시일 2026-10-03", "Version 1.0 · Published 2026-10-03"),
        ["main.website"] = ("사이트 : ", "Website : "),
        ["main.profile"] = ("프로필", "Profile"),
        ["main.import"] = ("가져오기", "Import"),
        ["main.export"] = ("내보내기", "Export"),
        ["main.reset"] = ("프로필 모두 초기화", "Reset profile"),
        ["main.device"] = ("입력 장치", "Input device"),
        ["main.refresh"] = ("새로고침", "Refresh"),
        ["log.title"] = ("매핑 입력 로그", "Mapping input log"),
        ["log.hint"] = ("이 매핑 편집 창에서 사용하는 장치의 입력만 기록합니다. 편집 창을 닫으면 기록을 멈춥니다. 축은 전체 범위 1% 이상 변화 시 100ms 간격으로 기록하며, 버튼 누름·해제와 햇 방향 변화도 기록합니다. 화면에는 최근 2,000줄을 표시하고 [로그 파일 저장]은 이 편집 창의 기록 전체를 저장합니다.",
            "Records only the input device used in this mapping editor. Recording stops when the editor closes. Axes are logged at 100ms intervals when they change by at least 1% of their full range; button presses/releases and hat changes are also logged. The display keeps the latest 2,000 lines; Save log file exports this editor's full recording."),
        ["log.copy"] = ("표시 로그 복사", "Copy displayed log"),
        ["log.save"] = ("로그 파일 저장", "Save log file"),
        ["log.clear"] = ("화면 지우기", "Clear display"),
        ["log.autoScroll"] = ("자동 스크롤", "Auto-scroll"),
        ["main.games"] = ("게임 (게임마다 매핑)", "Games (mapping per game)"),
        ["main.search"] = ("🔍 검색", "🔍 Search"),
        ["main.searchTip"] = ("게임 이름 또는 AppID 검색", "Search by game name or AppID"),
        ["main.removeMapping"] = ("게임 매핑 삭제", "Delete game mapping"),
        ["main.status"] = ("Steam Input 상태", "Steam Input status"),
        ["main.analyze"] = ("기존 설정 분석", "Analyze settings"),
        ["main.apply"] = ("Steam에 적용", "Apply to Steam"),
        ["main.applyTip"] = ("게임 매핑으로 장치 설정을 만들어 이 조이스틱을 Steam 일반 컨트롤러로 등록하고, 게임별 설정을 씁니다",
            "Registers this joystick as a Steam generic controller using your game mappings and writes the per-game configs"),
        ["main.disable"] = ("Steam에서 비활성화", "Disable in Steam"),
        ["main.disableTip"] = ("장치 설정을 지워 Steam에서 일반 컨트롤러가 아닌 조이스틱으로 보이게 합니다",
            "Removes the device layout so Steam sees a plain joystick instead of a generic controller"),
        ["main.log"] = ("로그", "Log"),

        // 매핑 편집 창
        ["editor.viewLog"] = ("로그 보기", "View log"),
        ["editor.preset"] = ("AC8 예제 적용", "Apply AC8 example"),
        ["editor.presetTip"] = ("T.A320 Pilot으로 맞춘 ACE COMBAT 8 매핑", "ACE COMBAT 8 mapping made for the T.A320 Pilot"),
        ["editor.clearAll"] = ("모두 지우기", "Clear all"),
        ["editor.colInput"] = ("물리 입력 (조이스틱)", "Physical input (joystick)"),
        ["editor.colOutput"] = ("→ Xbox 출력", "→ Xbox output"),
        ["editor.colInvert"] = ("반전", "Invert"),
        ["editor.colDeadZone"] = ("데드존 / 시작 %", "Dead zone / start %"),
        ["editor.colOuter"] = ("외곽 / 끝 %", "Outer / end %"),
        ["editor.colLive"] = ("출력값", "Output"),
        ["editor.detect"] = ("감지", "Detect"),
        ["editor.clear"] = ("지우기", "Clear"),
        ["editor.addTip"] = ("이 출력에 입력 추가 (예: 쓰로틀과 버튼 둘 다 LT)", "Add another input for this output (e.g. throttle and a button both on LT)"),
        ["editor.rangeStartTip"] = ("감지 범위 시작 (축 %)", "Range start (axis %)"),
        ["editor.rangeEndTip"] = ("감지 범위 끝 (축 %)", "Range end (axis %)"),
        ["editor.monitor"] = ("실시간 입력 모니터", "Live input monitor"),
        ["editor.save"] = ("저장", "Save"),
        ["editor.cancel"] = ("취소", "Cancel"),
    };
}
