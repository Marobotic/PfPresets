using System;
using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace PfPresets
{
    /// <summary>
    /// Shared helpers for interacting with the game's native Atk UI:
    /// synthetic button clicks, dropdown lists, and fixed-size string buffers.
    /// </summary>
    public static unsafe class AtkHelpers
    {
        /// <summary>
        /// Simulates a click on a button component by sending a ButtonClick event to the addon.
        /// </summary>
        public static void ClickButton(AtkUnitBase* addon, AtkComponentButton* button)
        {
            if (addon == null || button == null) return;
            var resNode = GetOwnerResNode(&button->AtkComponentBase);
            if (resNode == null) return;
            SendSyntheticClick((AtkEventListener*)addon, resNode);
        }

        /// <summary>
        /// Simulates a click on a dropdown's toggle checkbox by sending a ButtonClick event
        /// to the dropdown component itself.
        /// </summary>
        public static void ClickDropDownToggle(AtkComponentDropDownList* dropdown)
        {
            if (dropdown == null || dropdown->Checkbox == null) return;
            var resNode = GetOwnerResNode(&((AtkComponentButton*)dropdown->Checkbox)->AtkComponentBase);
            if (resNode == null) return;
            SendSyntheticClick((AtkEventListener*)dropdown, resNode);
        }

        /// <summary>
        /// Faithful port of ECommons' ClickAddonButton (the exact method RecruitmentRefresher
        /// uses). Unlike <see cref="ClickButton"/>, it fires the button's own native click event
        /// (its real event type and event pointer) through the addon instead of synthesizing a
        /// forced ButtonClick. This is what makes the Edit -> Recruit clicks register reliably.
        /// </summary>
        public static bool ClickAddonButton(AtkUnitBase* addon, AtkComponentButton* button)
        {
            if (addon == null || button == null || !button->IsEnabled) return false;
            var ownerNode = button->AtkComponentBase.OwnerNode;
            if (ownerNode == null) return false;
            var btnRes = &ownerNode->AtkResNode;
            var evt = (AtkEvent*)btnRes->AtkEventManager.Event;
            if (evt == null) return false;
            addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, btnRes->AtkEventManager.Event);
            return true;
        }

        /// <summary>True when a dropdown's item list has been built by the game
        /// (i.e. it has at least one entry).</summary>
        /// <summary>The visible label of a component button, or empty when it has no text node.
        /// Used to find a button by what it says rather than by a hard-coded node id, which shifts
        /// between game patches.</summary>
        public static string GetButtonLabel(AtkComponentButton* button)
        {
            if (button == null || button->ButtonTextNode == null)
                return string.Empty;

            return button->ButtonTextNode->NodeText.ToString() ?? string.Empty;
        }

        public static bool IsListPopulated(AtkComponentDropDownList* dropdown)
        {
            return dropdown != null &&
                   dropdown->List != null &&
                   dropdown->List->GetItemCount() > 0;
        }

        /// <summary>Reads the label of a single dropdown item using the game's native accessor.</summary>
        public static string GetDropDownItemLabel(AtkComponentList* list, int index)
        {
            var cstr = list->GetItemLabel(index);
            if (cstr.Value == null) return string.Empty;
            return Marshal.PtrToStringUTF8((nint)cstr.Value) ?? string.Empty;
        }

        /// <summary>
        /// Selects an item in a dropdown using the game's own native SelectItem routine.
        /// This is the exact code path a manual click triggers, so the owning agent registers
        /// the selection correctly (something a raw memory write cannot do until the dropdown
        /// has been interacted with at least once per session).
        /// </summary>
        public static bool TrySelectDropDownItem(AtkComponentDropDownList* dropdown, int index)
        {
            int count = (dropdown != null && dropdown->List != null) ? dropdown->List->GetItemCount() : 0;
            if (dropdown == null || dropdown->List == null || index < 0 || index >= count)
                return false;

            // Update both the displayed text and the underlying list selection so the in-game
            // dropdown visually reflects the choice.
            dropdown->SelectItem(index);
            dropdown->List->SelectItem(index, true);
            return true;
        }

        /// <summary>
        /// Opens a dropdown's list. Sends only the click event: calling SetChecked(true) first
        /// as well double-toggles the checkbox, which pops the list open just long enough to
        /// build it and then immediately closes it again.
        /// </summary>
        public static void OpenDropDownList(AtkComponentDropDownList* dropdown)
        {
            if (dropdown == null || dropdown->Checkbox == null) return;
            if (dropdown->IsOpen) return;
            ClickDropDownToggle(dropdown);
        }

        /// <summary>Closes a dropdown's list by toggling its checkbox closed.</summary>
        public static void CloseDropDownList(AtkComponentDropDownList* dropdown)
        {
            if (dropdown == null || dropdown->Checkbox == null) return;
            if (!dropdown->IsOpen) return;
            dropdown->Checkbox->SetChecked(false);
            ClickDropDownToggle(dropdown);
        }

        /// <summary>
        /// Writes raw bytes into a fixed-size, null-terminated buffer.
        ///
        /// Bytes rather than a string, and deliberately the only way in: these buffers hold
        /// SeString, not text. An auto-translate phrase is a payload no string can round-trip, so
        /// a convenient string overload here would quietly destroy one - which is exactly what the
        /// comment path used to do. Callers encode, and own the truncation: cutting a byte array
        /// blind can split a character or a payload in half.
        /// </summary>
        public static void SetFixedBytes(byte* dest, ReadOnlySpan<byte> bytes, int maxLength)
        {
            int len = Math.Min(bytes.Length, maxLength - 1);
            for (int i = 0; i < len; i++)
                dest[i] = bytes[i];
            dest[len] = 0;
        }

        /// <summary>Reads a UTF-8 string from a fixed-size, null-terminated buffer.</summary>
        public static string GetFixedString(byte* src, int maxLength)
        {
            int len = 0;
            while (len < maxLength && src[len] != 0)
                len++;
            return Encoding.UTF8.GetString(src, len);
        }

        /// <summary>The owner node's res node for a component, falling back to the component's
        /// own res node when there is no owner.</summary>
        private static AtkResNode* GetOwnerResNode(AtkComponentBase* component)
        {
            return component->OwnerNode != null
                ? &component->OwnerNode->AtkResNode
                : component->AtkResNode;
        }

        /// <summary>
        /// Sends a synthetic ButtonClick event to a listener. When the node already has a native
        /// event we copy it (preserving its parameters) and force the type to ButtonClick;
        /// otherwise a minimal event is built from scratch.
        /// </summary>
        private static void SendSyntheticClick(AtkEventListener* listener, AtkResNode* resNode)
        {
            var evt = (AtkEvent*)resNode->AtkEventManager.Event;
            if (evt != null)
            {
                AtkEvent simulatedEvent = *evt;
                simulatedEvent.State.EventType = AtkEventType.ButtonClick;
                simulatedEvent.State.ReturnFlags = 0;
                simulatedEvent.State.StateFlags = 0;
                listener->ReceiveEvent(AtkEventType.ButtonClick, (int)evt->Param, &simulatedEvent);
            }
            else
            {
                AtkEvent simulatedEvent = new AtkEvent
                {
                    Node = resNode,
                    Target = (AtkEventTarget*)&resNode->AtkEventTarget,
                    Listener = listener,
                    Param = resNode->NodeId,
                };
                simulatedEvent.State.EventType = AtkEventType.ButtonClick;
                simulatedEvent.State.ReturnFlags = 0;
                simulatedEvent.State.StateFlags = 0;
                listener->ReceiveEvent(AtkEventType.ButtonClick, (int)resNode->NodeId, &simulatedEvent);
            }
        }
    }
}
