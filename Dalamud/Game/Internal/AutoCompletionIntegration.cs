using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

using Dalamud.Game.Command;
using Dalamud.Hooking;
using Dalamud.Logging.Internal;
using Dalamud.Utility;

using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.Completion;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

namespace Dalamud.Game.Internal;

/// <summary>
/// This class adds Dalamud and plugin commands to the chat box's autocompletion.
/// </summary>
[ServiceManager.EarlyLoadedService]
internal sealed unsafe class AutoCompletionIntegration : IInternalDisposableService
{
    // 0xFF is a magic group number that causes CompletionModule's internals to treat entries
    // as raw strings instead of as lookups into an EXD sheet
    private const int GroupNumber = 0xFF;

    private static readonly ModuleLog Log = ModuleLog.Create<AutoCompletionIntegration>();

    [ServiceManager.ServiceDependency]
    private readonly CommandManager commandManager = Service<CommandManager>.Get();

    [ServiceManager.ServiceDependency]
    private readonly Framework framework = Service<Framework>.Get();

    private readonly Dictionary<string, EntryStrings> cachedCommands = [];

    private EntryStrings? dalamudCategory;

    private AsmHook? openSuggestionsHook;
    private CompletionUpdateDelegate? completionUpdateCallback;
    private Hook<CompletionModule.Delegates.GetSelection>? getSelectionHook;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoCompletionIntegration"/> class.
    /// </summary>
    [ServiceManager.ServiceConstructor]
    internal AutoCompletionIntegration()
    {
        this.framework.RunOnTick(this.Setup);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CompletionUpdateDelegate();

    /// <inheritdoc/>
    void IInternalDisposableService.DisposeService()
    {
        this.openSuggestionsHook?.Disable();
        this.openSuggestionsHook?.Dispose();

        this.getSelectionHook?.Disable();
        this.getSelectionHook?.Dispose();

        this.dalamudCategory?.Dispose();

        this.ClearCachedCommands();
    }

    private void Setup()
    {
        var uiModule = UIModule.Instance();
        if (uiModule == null || uiModule->FrameCount == 0)
        {
            this.framework.RunOnTick(this.Setup);
            return;
        }

        this.dalamudCategory = new EntryStrings("【Dalamud】");

        // The CN keyboard completion path is inlined and does not call OpenCompletion.
        // Use ottercorp's call-site signature, preserving the native state around our callback.
        var openSuggestionsAddress = Service<TargetSigScanner>.Get().ScanText(
            "4C 8D 86 ?? ?? ?? ?? 48 8B CE 48 8D 96 ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B 4E");
        this.completionUpdateCallback = this.UpdateCompletionDataSafely;
        var callbackAddress = Marshal.GetFunctionPointerForDelegate(this.completionUpdateCallback);
        this.openSuggestionsHook = new AsmHook(
            openSuggestionsAddress,
            [
                "use64",
                "pushfq", "push rax", "push rcx", "push rdx",
                "push r8", "push r9", "push r10", "push r11",
                // The call site is 16-byte aligned. Reserve shadow space and save volatile SIMD registers.
                "sub rsp, 0x80",
                "movdqu [rsp+0x20], xmm0", "movdqu [rsp+0x30], xmm1",
                "movdqu [rsp+0x40], xmm2", "movdqu [rsp+0x50], xmm3",
                "movdqu [rsp+0x60], xmm4", "movdqu [rsp+0x70], xmm5",
                $"mov rax, 0x{callbackAddress:X}", "call rax",
                "movdqu xmm0, [rsp+0x20]", "movdqu xmm1, [rsp+0x30]",
                "movdqu xmm2, [rsp+0x40]", "movdqu xmm3, [rsp+0x50]",
                "movdqu xmm4, [rsp+0x60]", "movdqu xmm5, [rsp+0x70]",
                "add rsp, 0x80",
                "pop r11", "pop r10", "pop r9", "pop r8",
                "pop rdx", "pop rcx", "pop rax", "popfq",
            ],
            "CN command completion");

        this.getSelectionHook = Hook<CompletionModule.Delegates.GetSelection>.FromAddress(
            (nint)uiModule->CompletionModule.VirtualTable->GetSelection,
            this.GetSelectionDetour);

        this.openSuggestionsHook.Enable();
        this.getSelectionHook.Enable();
    }

    private void UpdateCompletionDataSafely()
    {
        try
        {
            this.UpdateCompletionData();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to update CN command completion data");
        }
    }

    private int GetSelectionDetour(CompletionModule* thisPtr, CategoryData.CompletionDataStruct* dataStructs, int index, Utf8String* outputString, Utf8String* outputDisplayString)
    {
        var ret = this.getSelectionHook!.Original.Invoke(thisPtr, dataStructs, index, outputString, outputDisplayString);
        this.HandleInsert(ret, outputString, outputDisplayString);
        return ret;
    }

    private void UpdateCompletionData()
    {
        if (!this.TryGetActiveTextInput(out var component, out var addon))
        {
            if (this.HasDalamudCategory())
                this.ResetCompletionData();

            return;
        }

        var uiModule = UIModule.Instance();
        if (uiModule == null)
            return;

        this.ResetCompletionData();
        this.ClearCachedCommands();

        var currentText = component->EvaluatedString.StringPtr.ExtractText();

        var commands = this.commandManager.Commands
            .Where(kv => kv.Value.ShowInHelp && (currentText.Length == 0 || kv.Key.StartsWith(currentText)))
            .OrderBy(kv => kv.Key);

        if (!commands.Any())
            return;

        var categoryData = (CategoryData*)IMemorySpace.GetDefaultSpace()->Malloc(CategoryData.StructSize, 0x08);
        categoryData->Ctor(GroupNumber, 0xFF);

        uiModule->CompletionModule.AddCategoryData(
            GroupNumber,
            this.dalamudCategory!.Display->StringPtr,
            this.dalamudCategory.Match->StringPtr, categoryData);

        foreach (var (cmd, info) in commands)
        {
            if (!this.cachedCommands.TryGetValue(cmd, out var entryString))
                this.cachedCommands.Add(cmd, entryString = new EntryStrings(cmd));

            uiModule->CompletionModule.AddCompletionEntry(
                GroupNumber,
                0xFF,
                entryString.Display->StringPtr,
                entryString.Match->StringPtr,
                0xFF);
        }

        categoryData->SortEntries();
    }

    private void HandleInsert(int ret, Utf8String* outputString, Utf8String* outputDisplayString)
    {
        // -2 means it was a plain text final selection, so it might be ours.
        if (ret != -2 || outputString == null)
            return;

        // Strip out color payloads that we added to the string.
        var txt = outputString->StringPtr.ExtractText();
        if (!this.cachedCommands.ContainsKey(txt))
            return;

        if (!this.TryGetActiveTextInput(out _, out _))
        {
            outputString->Clear();

            if (outputDisplayString != null)
                outputDisplayString->Clear();

            return;
        }

        outputString->SetString(txt + ' ');
    }

    private bool TryGetActiveTextInput(out AtkComponentTextInput* component, out AtkUnitBase* addon)
    {
        component = null;
        addon = null;

        var raptureAtkModule = RaptureAtkModule.Instance();
        if (raptureAtkModule == null)
            return false;

        var textInputEventInterface = raptureAtkModule->TextInput.TargetTextInputEventInterface;
        if (textInputEventInterface == null)
            return false;

        var ownerNode = textInputEventInterface->GetOwnerNode();
        if (ownerNode == null || ownerNode->GetNodeType() != NodeType.Component)
            return false;

        var componentNode = (AtkComponentNode*)ownerNode;
        var componentBase = componentNode->Component;
        if (componentBase == null || componentBase->GetComponentType() != ComponentType.TextInput)
            return false;

        component = (AtkComponentTextInput*)componentBase;

        addon = component->OwnerAddon;

        if (addon == null)
            addon = component->ContainingAddon2;

        if (addon == null)
            addon = RaptureAtkUnitManager.Instance()->GetAddonByNode((AtkResNode*)component->OwnerNode);

        return addon != null && addon->Name.BeforeNull().SequenceEqual("ChatLog"u8);
    }

    private bool HasDalamudCategory()
    {
        var uiModule = UIModule.Instance();
        if (uiModule == null)
            return false;

        for (var i = 0; i < uiModule->CompletionModule.CategoryNames.Count; i++)
        {
            if (uiModule->CompletionModule.CategoryNames[i].AsReadOnlySeStringSpan().ContainsText("【Dalamud】"u8))
            {
                return true;
            }
        }

        return false;
    }

    private void ResetCompletionData()
    {
        var uiModule = UIModule.Instance();
        if (uiModule == null)
            return;

        uiModule->CompletionModule.ClearCompletionData();

        // This happens in UIModule.Update. Just repeat it to fill CompletionData back up with defaults.
        uiModule->CompletionModule.Update(
            &uiModule->CompletionSheetName,
            &uiModule->CompletionOpenIconMacro,
            &uiModule->CompletionCloseIconMacro,
            0);
    }

    private void ClearCachedCommands()
    {
        foreach (var entry in this.cachedCommands.Values)
        {
            entry.Dispose();
        }

        this.cachedCommands.Clear();
    }

    private class EntryStrings : IDisposable
    {
        public EntryStrings(string command)
        {
            using var rssb = new RentedSeStringBuilder();

            this.Display = Utf8String.FromSequence(rssb.Builder
                .PushColorType(539)
                .Append(command)
                .PopColorType()
                .GetViewAsSpan());

            this.Match = Utf8String.FromString(command);
        }

        public Utf8String* Display { get; }

        public Utf8String* Match { get; }

        public void Dispose()
        {
            this.Display->Dtor(true);
            this.Match->Dtor(true);
        }
    }
}
