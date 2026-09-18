using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.UI.Composition;

namespace CopilotBuddy.Composition;

internal sealed partial class ReplayWindow
{
    private const int WmPowerBroadcast = 0x0218;
    private const int PbtApmSuspend = 0x0004;
    private const int PbtApmResumeSuspend = 0x0007;
    private const int PbtApmResumeAutomatic = 0x0012;
    private const int PbtPowerSettingChange = 0x8013;
    private const uint DeviceNotifyWindowHandle = 0;
    private static readonly Guid ConsoleDisplayState =
        new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    private nint displayStateNotification;
    private bool sessionLocked;
    private bool powerSuspended;
    private bool displayOff;

    private void StartActivityMonitoring()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        displayStateNotification = RegisterPowerSettingNotification(
            Handle,
            in ConsoleDisplayState,
            DeviceNotifyWindowHandle);
        if (displayStateNotification == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private void StopActivityMonitoring()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        if (displayStateNotification != 0)
        {
            UnregisterPowerSettingNotification(displayStateNotification);
            displayStateNotification = 0;
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs args)
    {
        PostActivityUpdate(() =>
        {
            if (args.Mode == PowerModes.Suspend)
            {
                powerSuspended = true;
            }
            else if (args.Mode == PowerModes.Resume)
            {
                powerSuspended = false;
            }
            else
            {
                return;
            }
            UpdateBuddyActivity();
        });
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs args)
    {
        PostActivityUpdate(() =>
        {
            if (args.Reason is SessionSwitchReason.SessionLock or
                SessionSwitchReason.ConsoleDisconnect or
                SessionSwitchReason.RemoteDisconnect or
                SessionSwitchReason.SessionLogoff)
            {
                sessionLocked = true;
            }
            else if (args.Reason is SessionSwitchReason.SessionUnlock or
                SessionSwitchReason.ConsoleConnect or
                SessionSwitchReason.RemoteConnect or
                SessionSwitchReason.SessionLogon)
            {
                sessionLocked = false;
            }
            else
            {
                return;
            }
            UpdateBuddyActivity();
        });
    }

    private void PostActivityUpdate(Action action)
    {
        if (Disposing || IsDisposed || !IsHandleCreated)
        {
            return;
        }
        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException) when (Disposing || IsDisposed) { }
    }

    private bool HandleActivityMessage(ref Message message)
    {
        if (message.Msg != WmPowerBroadcast)
        {
            return false;
        }

        int powerEvent = message.WParam.ToInt32();
        if (powerEvent == PbtApmSuspend)
        {
            powerSuspended = true;
            UpdateBuddyActivity();
            message.Result = 1;
            return true;
        }
        if (powerEvent is PbtApmResumeSuspend or PbtApmResumeAutomatic)
        {
            powerSuspended = false;
            UpdateBuddyActivity();
            message.Result = 1;
            return true;
        }
        if (powerEvent != PbtPowerSettingChange || message.LParam == 0)
        {
            return false;
        }

        PowerBroadcastSetting setting = Marshal.PtrToStructure<PowerBroadcastSetting>(message.LParam);
        if (setting.PowerSetting != ConsoleDisplayState || setting.DataLength < sizeof(int))
        {
            return false;
        }

        int dataOffset = Marshal.OffsetOf<PowerBroadcastSetting>(
            nameof(PowerBroadcastSetting.Data)).ToInt32();
        displayOff = Marshal.ReadInt32(message.LParam, dataOffset) == 0;
        UpdateBuddyActivity();
        message.Result = 1;
        return true;
    }

    private void UpdateBuddyActivity()
    {
        bool active = !sessionLocked && !powerSuspended && !displayOff;
        if (buddyActive == active || controller is null)
        {
            buddyActive = active;
            return;
        }

        buddyActive = active;
        modelTimestamp = Stopwatch.GetTimestamp();
        needsTimestamp = modelTimestamp;
        if (active)
        {
            ResumeBuddy();
        }
        else
        {
            PauseBuddy();
        }
    }

    private void PauseBuddy()
    {
        passiveTimer.Stop();
        pointerTimer.Stop();
        needsTimer.Stop();
        presentTimer.Stop();
        inputTimer.Stop();
        focusWaitTimer.Stop();
        sparkleTimer.Stop();
        Capture = false;
        pointerPending = false;
        attentionPressPending = false;
        presentPressPending = false;
        SetBuddyAnimationsPaused(true);
        UpdatePointerRouting();
    }

    private void ResumeBuddy()
    {
        modelTimestamp = Stopwatch.GetTimestamp();
        needsTimestamp = modelTimestamp;
        SetBuddyAnimationsPaused(false);
        needsTimer.Start();
        presentTimer.Start();
        pointerTimer.Start();
        if (focusingSession)
        {
            focusWaitTimer.Start();
        }
        if (sparkleResources.Count > 0)
        {
            sparkleTimer.Start();
        }
        RefreshAttention();
        StartPassiveMotion();
        UpdatePointerRouting();
    }

    private void SetBuddyAnimationsPaused(bool paused)
    {
        if (sprite is not null)
        {
            SetAnimationPaused(sprite, nameof(sprite.Offset), paused);
            SetAnimationPaused(sprite, nameof(sprite.Scale), paused);
        }
        foreach (ContainerVisual frame in frameVisuals.Values)
        {
            SetAnimationPaused(frame, nameof(frame.Opacity), paused);
        }

        bool pauseSupply = paused || supplyPausedForAlert;
        foreach (SupplyItem supply in supplies.Values)
        {
            supply.SetPaused(pauseSupply);
        }
        handoffPresent?.SetPaused(paused);
        foreach (StoredPresent stored in storedPresents.Values)
        {
            stored.Present.SetPaused(paused);
        }

        SetParticleAnimationsPaused(foodParticleResources, pauseSupply);
        SetParticleAnimationsPaused(waterParticleResources, pauseSupply);
        SetParticleAnimationsPaused(sweatResources, paused);
        SetParticleAnimationsPaused(sparkleResources, paused);
        if (heartVisual is not null)
        {
            SetAnimationPaused(heartVisual, nameof(heartVisual.Offset), paused);
            SetAnimationPaused(heartVisual, nameof(heartVisual.Opacity), paused);
        }

        foreach (CompositionPropertySet motion in orbResources.OfType<CompositionPropertySet>())
        {
            SetAnimationPaused(motion, "Bob", paused);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerBroadcastSetting
    {
        public Guid PowerSetting;
        public int DataLength;
        public byte Data;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint RegisterPowerSettingNotification(
        nint recipient,
        in Guid powerSettingGuid,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterPowerSettingNotification(nint handle);
}
