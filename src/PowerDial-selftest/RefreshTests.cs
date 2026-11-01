using System.Collections.Generic;
using PowerDial;

sealed partial class SelfTest
{
    static void RefreshChecks()
    {
        Check("refresh guards block mouse clicks and keyboard actions", RefreshInputGate.IsInteraction(0x201, 0) &&
            RefreshInputGate.IsInteraction(0x202, 0) && RefreshInputGate.IsInteraction(0x100, 0));
        Check("refresh guards allow painting and normal hover", !RefreshInputGate.IsInteraction(0xF, 0) &&
            !RefreshInputGate.IsInteraction(0x200, 0));
        Check("refresh guards block dragging and wheel edits", RefreshInputGate.IsInteraction(0x200, 1) &&
            RefreshInputGate.IsInteraction(0x20A, 0));
        List<Suggestion> a = new List<Suggestion> { new Suggestion { Id = "epp", Target = 80, Current = 20, Info = "why", Kind = FixKind.Setting } };
        List<Suggestion> b = new List<Suggestion> { new Suggestion { Id = "epp", Target = 80, Current = 20, Info = "why", Kind = FixKind.Setting } };
        Check("equivalent scans retain suggestion cards", RefreshState.SameSuggestions(a, b));
        b[0].Target = 90;
        Check("changed fix targets require new action cards", !RefreshState.SameSuggestions(a, b));
        b[0].Target = 80; b[0].OpenUri = "ms-settings:appsfeatures";
        Check("changed destinations refresh action cards", !RefreshState.SameSuggestions(a, b));
        b[0].OpenUri = null; b[0].Info = "new explanation";
        Check("changed explanations refresh suggestion cards", !RefreshState.SameSuggestions(a, b));
        List<Finding> first = new List<Finding> { new Finding { Name = "GPU", Ok = true, Detail = "asleep" } };
        List<Finding> second = new List<Finding> { new Finding { Name = "GPU", Ok = true, Detail = "asleep" } };
        Check("equivalent GPU results retain rows", RefreshState.SameFindings(first, second));
        second[0].Ok = false;
        Check("changed GPU state refreshes rows", !RefreshState.SameFindings(first, second));
    }
}
