using System.Runtime.CompilerServices;
using SteamJoystickMapper.Localization;

namespace SteamJoystickMapper.Tests;

/// <summary>테스트는 한국어 메시지를 검사하므로 시스템 언어와 관계없이 한국어로 고정한다.</summary>
internal static class TestLanguage
{
    [ModuleInitializer]
    internal static void Init() => Loc.Init("ko");
}
