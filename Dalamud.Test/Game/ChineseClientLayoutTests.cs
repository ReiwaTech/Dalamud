using System;
using System.Reflection;
using System.Runtime.InteropServices;

using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

using Xunit;

namespace Dalamud.Test.Game;

public class ChineseClientLayoutTests
{
    // Native getter instructions and live memory from CN 2026.09.15.0000.0000.
    // In particular, UI3DModule and RaptureAtkModule must NOT inherit the later -0x10 shift.
    [Theory]
    [InlineData(typeof(UIModule), "UI3DModule", 0xBB150)]
    [InlineData(typeof(UIModule), "RaptureAtkModule", 0xD2690)]
    [InlineData(typeof(UIModule), "InfoModule", 0xFD000)]
    [InlineData(typeof(UIModule), "UIModuleHelpers", 0xFEC78)]
    [InlineData(typeof(UIModule), "UIInputData", 0xFF020)]
    [InlineData(typeof(UIModule), "UIInputModule", 0xFFA50)]
    [InlineData(typeof(AtkModule), "TextInput", 0x72F0)]
    [InlineData(typeof(RaptureAtkModule), "UIModulePtr", 0x12418)]
    [InlineData(typeof(RaptureAtkModule), "AgentModule", 0x12428)]
    [InlineData(typeof(RaptureAtkModule), "RaptureHotbarModulePtr", 0x13440)]
    [InlineData(typeof(RaptureAtkModule), "NameplateInfoCount", 0x1D400)]
    [InlineData(typeof(RaptureAtkModule), "LocalPlayerClassJobId", 0x2A8A8)]
    public void LayoutMatchesChineseClient(Type type, string field, int nativeOffset)
    {
        // These are explicit native layouts, not CLR-marshaled structures.
        Assert.Equal(LayoutKind.Explicit, type.StructLayoutAttribute!.Value);
        var member = type.GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(member);
        Assert.Equal(nativeOffset, member.GetCustomAttribute<FieldOffsetAttribute>()!.Value);
    }
}
