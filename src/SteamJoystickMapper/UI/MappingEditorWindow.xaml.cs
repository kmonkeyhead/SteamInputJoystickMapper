using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SteamJoystickMapper.Devices;
using SteamJoystickMapper.Logging;
using SteamJoystickMapper.Mapping;
using SteamJoystickMapper.Steam;

namespace SteamJoystickMapper.UI;

public partial class MappingEditorWindow : Window
{
    private static readonly (string Header, XboxOutput[] Outputs)[] Sections =
    {
        ("Sticks", new[] { XboxOutput.LeftStickX, XboxOutput.LeftStickY, XboxOutput.RightStickX, XboxOutput.RightStickY }),
        ("Triggers", new[] { XboxOutput.LT, XboxOutput.RT }),
        ("Buttons", new[] { XboxOutput.A, XboxOutput.B, XboxOutput.X, XboxOutput.Y, XboxOutput.LB, XboxOutput.RB,
                            XboxOutput.LS, XboxOutput.RS, XboxOutput.View, XboxOutput.Menu }),
        ("D-Pad", new[] { XboxOutput.DPadUp, XboxOutput.DPadDown, XboxOutput.DPadLeft, XboxOutput.DPadRight }),
    };

    private readonly ObservableCollection<BindingRow> _rows = new();
    private readonly ObservableCollection<AxisMonitorItem> _axisItems = new();
    private readonly ObservableCollection<ButtonMonitorItem> _buttonItems = new();
    private readonly DeviceInfo? _device;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly InputDetector _detector = new();
    private InputReader? _reader;
    private InputSnapshot? _last;
    private BindingRow? _detectRow;
    private DateTime _detectStarted;

    private readonly MapperProfile _profile;
    private readonly GameMapping _game;

    /// <summary>저장 시 결과 매핑.</summary>
    public List<MappingBinding> Result { get; private set; } = new();

    public MappingEditorWindow(MapperProfile profile, GameMapping game, DeviceInfo? device)
    {
        InitializeComponent();
        _profile = profile;
        _game = game;
        _device = device;

        Title = $"매핑 편집 - {game.GameName}";
        ModeText.Text = $"{game.GameName} ({game.AppId})";
        HintText.Text = "이 게임에서 쓸 매핑입니다. 비워 둔 출력은 이 게임에서 동작하지 않습니다. " +
                        "버튼 출력에 축을 움직이면 그 방향(−/+)이 버튼이 되며, 데드존 %를 넘게 꺾어야 눌립니다 " +
                        "(그 축의 스틱은 이 게임에서 스틱으로 쓸 수 없음). " +
                        "LT/RT [감지]: 쓰로틀을 쓸 방향으로 움직이면 0%쪽은 − 0~40%(0%에서 최대), 100%쪽은 + 60~100%(100%에서 최대)로 들어갑니다. " +
                        "범위는 시작/끝 % 칸에서 고칠 수 있습니다. 축 반전은 모든 게임이 같아야 합니다.";
        HeaderText.Text = $"{profile.ProfileName}   ·   {profile.Device.Name} [{profile.Device.Vid}:{profile.Device.Pid}]";

        var bindings = game.Bindings;
        foreach (var (header, outputs) in Sections)
        {
            _rows.Add(BindingRow.Header(header));
            foreach (var o in outputs)
            {
                // 출력 하나에 입력이 여러 개일 수 있다 (예: 쓰로틀 − 와 버튼 5 둘 다 LT): 입력마다 한 줄
                var existing = bindings.Where(b => b.Target == o).ToList();
                if (existing.Count == 0) _rows.Add(NewRow(o));
                foreach (var b in existing)
                {
                    var row = NewRow(o);
                    row.Load(b);
                    _rows.Add(row);
                }
            }
        }
        BindingGrid.ItemsSource = _rows;
        AxisMonitor.ItemsSource = _axisItems;
        ButtonMonitor.ItemsSource = _buttonItems;

        SourceInitialized += (_, _) => StartReader();
        Closed += (_, _) => { _timer.Stop(); _reader?.Dispose(); };
        _timer.Tick += (_, _) => Tick();
        Revalidate();
    }
    private BindingRow NewRow(XboxOutput target)
    {
        var row = new BindingRow(target);
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(BindingRow.Invert) or nameof(BindingRow.DeadZone) or nameof(BindingRow.OuterDeadZone)
                or nameof(BindingRow.RangeLow) or nameof(BindingRow.RangeHigh)) Revalidate();
        };
        return row;
    }

    /// <summary>같은 출력에 입력 줄을 하나 더 추가하고 바로 감지를 시작한다.</summary>
    private void AddInput_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BindingRow row || !row.IsEditable) return;
        var last = _rows.LastOrDefault(r => r.IsEditable && r.Target == row.Target)!;
        var added = NewRow(row.Target);
        _rows.Insert(_rows.IndexOf(last) + 1, added);
        BindingGrid.SelectedItem = added;
        StartDetect(added);
    }

    // ---------------- 장치 폴링 ----------------

    private void StartReader()
    {
        if (_device == null)
        {
            MonitorStatusText.Text = "프로필의 장치가 연결되어 있지 않습니다. 입력 감지와 모니터를 사용할 수 없습니다.";
            return;
        }
        try
        {
            _reader = new InputReader(_device, new WindowInteropHelper(this).Handle);
            foreach (var axis in _device.Axes) _axisItems.Add(new AxisMonitorItem(axis));
            for (var i = 0; i < _device.ButtonCount; i++) _buttonItems.Add(new ButtonMonitorItem(i));
            MonitorStatusText.Text = _device.Display;
            _timer.Start();
        }
        catch (Exception ex)
        {
            MonitorStatusText.Text = $"장치를 열 수 없습니다: {ex.Message}";
            AppLog.Warn($"입력 장치 열기 실패: {ex.Message}");
        }
    }

    private void Tick()
    {
        var snap = _reader?.Poll();
        if (snap == null)
        {
            MonitorStatusText.Text = "장치 응답 없음 (분리되었나요?)";
            return;
        }
        _last = snap;

        foreach (var item in _axisItems) item.Update(snap.Axes.GetValueOrDefault(item.Axis));
        for (var i = 0; i < _buttonItems.Count && i < snap.Buttons.Length; i++) _buttonItems[i].Pressed = snap.Buttons[i];
        PovMonitorText.Text = string.Join("\n", snap.Povs.Select((p, i) => $"POV {i}   {InputSnapshot.PovToDirection(p)?.ToString().ToUpperInvariant() ?? "-"}"));
        foreach (var row in _rows.Where(r => r.HasSource)) row.UpdateLive(snap);

        if (_detectRow != null) DetectTick(snap);
    }

    // ---------------- 입력 감지 ----------------

    private void StartDetect(BindingRow row)
    {
        if (_reader == null || _last == null)
        {
            DetectStatusText.Text = "장치가 연결되어 있지 않아 감지할 수 없습니다.";
            return;
        }
        StopDetect();
        _detectRow = row;
        row.IsDetecting = true;
        _detector.Reset(_last);
        _detectStarted = DateTime.Now;
        DetectStatusText.Text = XboxOutputInfo.KindOf(row.Target) == XboxOutputKind.Trigger
            ? $"{row.TargetText}: 쓰로틀을 쓸 방향으로 움직이세요 (0%쪽 → 0~40%, 100%쪽 → 60~100%). 버튼을 눌러도 됩니다."
            : $"{row.TargetText}: 입력을 기다리는 중...";
    }

    private void StopDetect()
    {
        if (_detectRow != null) _detectRow.IsDetecting = false;
        _detectRow = null;
    }

    private void DetectTick(InputSnapshot snap)
    {
        var row = _detectRow!;
        if (DateTime.Now - _detectStarted > TimeSpan.FromSeconds(15))
        {
            DetectStatusText.Text = "입력이 감지되지 않았습니다.";
            StopDetect();
            return;
        }
        var input = _detector.Detect(snap, preferAxis: XboxOutputInfo.KindOf(row.Target) == XboxOutputKind.Trigger);
        if (input == null) return;

        var kind = XboxOutputInfo.KindOf(row.Target);
        if (kind == XboxOutputKind.Button && input.Kind == PhysicalInputKind.Axis)
        {
            // 움직인 방향만 버튼으로 (예: 트위스트 왼쪽 → Twist (Rz) −)
            input = PhysicalInput.FromAxisHalf(input.Axis!.Value, _detector.AxisDirection(snap, input.Axis.Value));
        }
        else if (kind == XboxOutputKind.Trigger && input.Kind == PhysicalInputKind.Axis)
        {
            // 축 → 트리거는 움직인 방향(행위)으로 정한다: 0%쪽으로 움직이면 −, 100%쪽이면 +.
            // 범위는 기본값(− 0~40%, + 60~100%)으로 넣고 숫자 칸에서 고친다.
            input = PhysicalInput.FromAxisHalf(input.Axis!.Value, _detector.AxisDirection(snap, input.Axis.Value));
        }
        var compatible = kind switch
        {
            XboxOutputKind.StickAxis => input.Kind == PhysicalInputKind.Axis,
            XboxOutputKind.Trigger => true,
            _ => true,
        };
        if (!compatible)
        {
            DetectStatusText.Text = $"{input.DisplayName} detected — 스틱 출력에는 축을 움직여 주세요.";
            _detector.Reset(snap);
            return;
        }


        // 같은 물리 입력이 다른 출력에 이미 연결되어 있으면 옮긴다
        foreach (var other in _rows.Where(r => r != row && r.Source == input)) other.Clear();
        row.SetSource(input);
        DetectStatusText.Text = $"{input.DisplayName} detected → {row.TargetText}";
        StopDetect();
        Revalidate();
    }

    private void Detect_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BindingRow row && row.IsEditable) StartDetect(row);
    }

    private void BindingGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (BindingGrid.SelectedItem is BindingRow row && row.IsEditable && e.OriginalSource is not TextBox) StartDetect(row);
    }

    private void ClearRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BindingRow row) return;
        // 같은 출력의 추가 줄이면 줄째 지우고, 마지막 한 줄이면 비운다
        if (_rows.Count(r => r.IsEditable && r.Target == row.Target) > 1) _rows.Remove(row);
        else row.Clear();
        Revalidate();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _detectRow != null)
        {
            StopDetect();
            DetectStatusText.Text = "감지 취소";
            e.Handled = true;
        }
    }

    // ---------------- 프리셋 / 편집 ----------------

    private void ApplyBindings(IEnumerable<MappingBinding> bindings)
    {
        foreach (var b in bindings)
        {
            var row = _rows.FirstOrDefault(r => r.IsEditable && r.Target == b.Target);
            if (row == null) continue;
            foreach (var other in _rows.Where(r => r != row && r.Source == b.Source)) other.Clear();
            row.Load(b);
        }
        Revalidate();
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.Clear();
        ApplyBindings(Presets.AceCombatFlightStick(_profile.Device));
        DetectStatusText.Text = "프리셋 적용: Stick X/Y → 왼쪽 스틱, Twist → 오른쪽 스틱 X, 버튼 1~4 → B/A/X/Y, Hat → D-Pad";
    }

    private void PovToDpad_Click(object sender, RoutedEventArgs e)
    {
        if (_profile.Device.PovCount == 0)
        {
            DetectStatusText.Text = "이 장치에는 POV가 없습니다.";
            return;
        }
        ApplyBindings(Presets.PovToDpad(0));
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.Clear();
        Revalidate();
    }

    private List<MappingBinding> CurrentBindings() => _rows.Where(r => r.HasSource).Select(r => r.ToBinding()).ToList();

    /// <summary>편집 중인 내용을 반영한 프로필 사본으로 검증한다.</summary>
    private Steam.ValidationResult Validate()
    {
        var copy = new MapperProfile
        {
            ProfileName = _profile.ProfileName,
            Device = _profile.Device,
            Games = _profile.Games.Select(g => g.AppId == _game.AppId
                ? new GameMapping { AppId = g.AppId, GameName = g.GameName, Bindings = CurrentBindings() }
                : g).ToList(),
        };
        if (copy.FindGame(_game.AppId) == null)
            copy.Games.Add(new GameMapping { AppId = _game.AppId, GameName = _game.GameName, Bindings = CurrentBindings() });

        // 이 게임 항목 + 게임 공통 항목(축 반전 불일치 등)만 표시
        var full = SteamConfigValidator.ValidateMapper(copy);
        var prefix = $"[{_game.GameName}] ";
        static IEnumerable<string> Pick(IEnumerable<string> items, string prefix) =>
            items.Where(x => x.StartsWith(prefix) || !x.StartsWith('[')).Select(x => x.StartsWith(prefix) ? x[prefix.Length..] : x);
        var r = new Steam.ValidationResult();
        r.Errors.AddRange(Pick(full.Errors, prefix));
        r.Warnings.AddRange(Pick(full.Warnings, prefix));
        return r;
    }

    private void Revalidate()
    {
        if (!IsInitialized || ValidationText == null) return;
        var v = Validate();
        if (v.Errors.Count == 0 && v.Warnings.Count == 0)
        {
            ValidationText.Text = "✔ 검증 통과";
            ValidationText.Foreground = Brushes.DarkGreen;
            return;
        }
        ValidationText.Foreground = v.Errors.Count > 0 ? Brushes.Firebrick : Brushes.DarkGoldenrod;
        ValidationText.Text = string.Join("\n", v.Errors.Select(x => "✖ " + x).Concat(v.Warnings.Select(x => "⚠ " + x)));
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var v = Validate();
        if (!v.IsValid && MessageBox.Show(this, "검증 오류가 있습니다. 저장은 가능하지만 오류를 고치기 전에는 Steam에 적용할 수 없습니다.\n저장할까요?",
                "저장", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        Result = CurrentBindings();
        DialogResult = true;
    }
}
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public sealed class BindingRow : Observable
{
    private PhysicalInput? _source;
    private bool _invert;
    private int _deadZone;
    private int _outerDeadZone;
    private bool _isDetecting;
    private double _liveValue = -1;
    private readonly string? _headerText;

    public BindingRow(XboxOutput target) => Target = target;

    private BindingRow(string header)
    {
        _headerText = header;
        IsHeader = true;
    }

    public static BindingRow Header(string text) => new(text);

    public XboxOutput Target { get; }
    public bool IsHeader { get; }
    public bool IsEditable => !IsHeader;
    public string TargetText => IsHeader ? "" : XboxOutputInfo.DisplayName(Target);
    public string SourceText => IsHeader ? _headerText! : _source == null ? "—" : _source.DisplayName + RangeText;
    /// <summary>쓰로틀 → LT/RT의 감지 범위와 최대 지점 (축 %).</summary>
    private string RangeText => !ShowRange ? "" :
        _source!.AxisSign < 0
            ? $"  ({RangeLow}~{Math.Min(RangeHigh, 50)}%, {RangeLow}%에서 최대{(RangeHigh > 50 ? ", − 는 50%까지" : "")})"
            : $"  ({RangeLow}~{RangeHigh}%, {RangeHigh}%에서 최대)";

    /// <summary>방향 있는 축 → LT/RT: 데드존 대신 감지 범위 시작~끝(축 %)을 입력한다.</summary>
    public bool ShowRange => IsAxisToTrigger && _source is { IsHalfAxis: true };
    private int _rangeLow, _rangeHigh = 100;
    public int RangeLow { get => _rangeLow; set { if (Set(ref _rangeLow, value)) Raise(nameof(SourceText)); } }
    public int RangeHigh { get => _rangeHigh; set { if (Set(ref _rangeHigh, value)) Raise(nameof(SourceText)); } }
    public FontWeight SourceWeight => IsHeader ? FontWeights.Bold : FontWeights.Normal;

    public PhysicalInput? Source => _source;
    public bool HasSource => _source != null;
    public bool HasAxisOptions => _source?.Kind == PhysicalInputKind.Axis;
    /// <summary>축 → 버튼: 데드존(%)을 넘어야 버튼이 눌린다.</summary>
    public bool IsAxisToButton => _source is { IsHalfAxis: true } && XboxOutputInfo.KindOf(Target) == XboxOutputKind.Button;
    /// <summary>축 → 트리거: 데드존(%) = Steam "트리거 범위 시작". 축 한쪽 방향이면 가운데에서부터의 비율.</summary>
    public bool IsAxisToTrigger => HasAxisOptions && XboxOutputInfo.KindOf(Target) == XboxOutputKind.Trigger;
    public bool HasDeadZone => HasAxisOptions && (XboxOutputInfo.KindOf(Target) == XboxOutputKind.StickAxis || IsAxisToButton || IsAxisToTrigger);
    /// <summary>데드존 입력 칸 표시 (스틱 출력, 축 → 버튼, 방향 없는 축 → 트리거).</summary>
    public bool ShowDeadZone => HasDeadZone && !ShowRange;
    /// <summary>외곽 데드존은 스틱 출력에만 의미가 있다.</summary>
    public bool ShowOuterDeadZone => ShowDeadZone && XboxOutputInfo.KindOf(Target) == XboxOutputKind.StickAxis;
    /// <summary>반전은 장치 설정에 들어가므로 모든 게임에서 같아야 한다 (축 한쪽 방향은 반전 없음).</summary>
    public bool InvertEditable => HasAxisOptions && !_source!.IsHalfAxis;

    public bool Invert { get => _invert; set => Set(ref _invert, value); }
    public int DeadZone { get => _deadZone; set { if (Set(ref _deadZone, value)) Raise(nameof(SourceText)); } }
    public int OuterDeadZone { get => _outerDeadZone; set => Set(ref _outerDeadZone, value); }
    public bool IsDetecting { get => _isDetecting; set => Set(ref _isDetecting, value); }
    public double LiveValue { get => _liveValue; private set => Set(ref _liveValue, value); }

    public void SetSource(PhysicalInput input)
    {
        var wasAxis = HasAxisOptions;
        var wasHalf = _source?.IsHalfAxis ?? false;
        _source = input;
        if ((!wasAxis && HasAxisOptions) || wasHalf != input.IsHalfAxis)
        {
            Invert = false;
            // 축 → 버튼은 절반쯤 돌렸을 때 눌리도록 50%
            DeadZone = !ShowDeadZone ? 0 : IsAxisToButton ? 50 : IsAxisToTrigger ? 0 : 5;
            OuterDeadZone = 0;
        }
        // 쓰로틀 → LT/RT 기본 범위: − 0~40%, + 60~100%
        if (ShowRange) (RangeLow, RangeHigh) = input.AxisSign < 0 ? (0, 40) : (60, 100);
        RaiseSource();
    }

    public void Load(MappingBinding b)
    {
        _source = b.Source;
        Invert = b.Invert;
        DeadZone = b.DeadZone;
        OuterDeadZone = b.OuterDeadZone;
        (RangeLow, RangeHigh) = b.TriggerRange();
        RaiseSource();
    }

    public void Clear()
    {
        _source = null;
        Invert = false;
        DeadZone = 0;
        OuterDeadZone = 0;
        LiveValue = -1;
        RaiseSource();
    }

    private void RaiseSource()
    {
        Raise(nameof(Source));
        Raise(nameof(SourceText));
        Raise(nameof(HasSource));
        Raise(nameof(HasAxisOptions));
        Raise(nameof(HasDeadZone));
        Raise(nameof(ShowDeadZone));
        Raise(nameof(IsAxisToButton));
        Raise(nameof(IsAxisToTrigger));
        Raise(nameof(ShowOuterDeadZone));
        Raise(nameof(ShowRange));
        Raise(nameof(InvertEditable));
    }

    public MappingBinding ToBinding() => new()
    {
        Target = Target,
        Source = _source!,
        Invert = HasAxisOptions && Invert,
        DeadZone = ShowDeadZone ? DeadZone : 0,
        OuterDeadZone = ShowOuterDeadZone ? OuterDeadZone : 0,
        RangeLow = ShowRange ? RangeLow : null,
        RangeHigh = ShowRange ? RangeHigh : null,
    };

    /// <summary>현재 입력으로 이 출력이 어떤 값이 될지 미리보기 (-1..1).</summary>
    public void UpdateLive(InputSnapshot snap)
    {
        if (_source == null) return;
        double v;
        switch (_source.Kind)
        {
            case PhysicalInputKind.Axis when _source.IsHalfAxis && IsAxisToTrigger:
            {
                // 쓰로틀 → LT/RT: 감지 범위 안에서 최대 지점 쪽으로 갈수록 커짐 (0..1을 막대 -1..1에 표시)
                var pct = (snap.Axes.GetValueOrDefault(_source.Axis!.Value) + 1) * 50;
                // − 방향은 반쪽 축이라 50%까지만 (Steam이 트리거 축 뒤집기를 지원하지 않음)
                var high = _source.AxisSign < 0 ? Math.Min(RangeHigh, 50) : RangeHigh;
                var span = Math.Max(1, high - RangeLow);
                var outVal = _source.AxisSign < 0 ? (high - pct) / span : (pct - RangeLow) / span;
                v = Math.Clamp(outVal, 0, 1) * 2 - 1;
                break;
            }
            case PhysicalInputKind.Axis when _source.IsHalfAxis:
                // 축 → 버튼: 그 방향으로 데드존을 넘으면 눌림
                v = snap.Axes.GetValueOrDefault(_source.Axis!.Value) * _source.AxisSign!.Value;
                v = v > DeadZone / 100.0 ? 1 : -1;
                break;
            case PhysicalInputKind.Axis:
                v = snap.Axes.GetValueOrDefault(_source.Axis!.Value);
                if (Invert) v = -v;
                var dz = DeadZone / 100.0;
                var outer = 1 - OuterDeadZone / 100.0;
                var mag = Math.Abs(v);
                v = mag < dz ? 0 : Math.Sign(v) * Math.Min(1, (mag - dz) / Math.Max(0.01, outer - dz));
                break;
            case PhysicalInputKind.Button:
                v = _source.Button < snap.Buttons.Length && snap.Buttons[_source.Button!.Value] ? 1 : -1;
                break;
            default:
                v = _source.Pov < snap.Povs.Length && InputSnapshot.PovToDirection(snap.Povs[_source.Pov!.Value]) == _source.Direction ? 1 : -1;
                break;
        }
        LiveValue = v;
    }
}

public sealed class AxisMonitorItem(JoyAxis axis) : Observable
{
    private double _value;
    public JoyAxis Axis { get; } = axis;
    public string Name { get; } = PhysicalInput.AxisDisplayName(axis);
    public double Value => _value;
    public string Text => Axis is JoyAxis.Slider0 or JoyAxis.Slider1 ? $"{(_value + 1) * 50:0}%" : $"{_value:+0.00;-0.00}";

    public void Update(double v)
    {
        if (Math.Abs(v - _value) < 0.001) return;
        _value = v;
        Raise(nameof(Value));
        Raise(nameof(Text));
    }
}

public sealed class ButtonMonitorItem(int index) : Observable
{
    private static readonly Brush On = new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0x4C));
    private bool _pressed;
    public string Label { get; } = (index + 1).ToString();
    public bool Pressed { get => _pressed; set { if (Set(ref _pressed, value)) Raise(nameof(Background)); } }
    public Brush Background => _pressed ? On : Brushes.White;
}
