using VoSharp.Kernel.Pool;

namespace VoSharp.Kernel.Events;

public class SlotStateChangedEventArgs : EventArgs
{
    public string SlotId { get; }
    public SlotState OldState { get; }
    public SlotState NewState { get; }
    public ModemSlot Slot { get; }

    public SlotStateChangedEventArgs(string slotId, SlotState oldState, SlotState newState, ModemSlot slot)
    {
        SlotId = slotId;
        OldState = oldState;
        NewState = newState;
        Slot = slot;
    }
}

public class ActiveSlotChangedEventArgs : EventArgs
{
    public string? PreviousSlotId { get; }
    public string? ActiveSlotId { get; }
    public ModemSlot? ActiveSlot { get; }

    public ActiveSlotChangedEventArgs(string? previousSlotId, string? activeSlotId, ModemSlot? activeSlot)
    {
        PreviousSlotId = previousSlotId;
        ActiveSlotId = activeSlotId;
        ActiveSlot = activeSlot;
    }
}

public class SlotListChangedEventArgs : EventArgs
{
    public IReadOnlyList<ModemSlot> Slots { get; }

    public SlotListChangedEventArgs(IReadOnlyList<ModemSlot> slots)
    {
        Slots = slots;
    }
}
