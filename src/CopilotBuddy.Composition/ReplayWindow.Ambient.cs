namespace CopilotBuddy.Composition;

internal sealed partial class ReplayWindow
{
    private static readonly string[] AmbientQuips =
    [
        "Still here. Standards remain negotiable.",
        "I have reviewed the taskbar. It persists.",
        "Nothing is on fire. A strong start.",
        "Standing by. Mostly standing.",
        "I remain professionally tiny.",
        "The pixels are holding.",
        "Quiet shift. Suspicious, but welcome.",
        "I have no updates. This is the update.",
        "All systems nominal enough.",
        "Time continues. Bold strategy.",
        "I am monitoring the lower edge.",
        "No crisis detected. I checked twice.",
        "The desktop appears committed.",
        "I await events with measured enthusiasm.",
        "Another minute successfully contained.",
        "The taskbar has not escaped.",
        "I remain calm on technical grounds.",
        "Progress is occurring somewhere.",
        "No alarms. I feel underutilized.",
        "The situation is rectangular.",
        "I have maintained my position.",
        "Everything seems fine from down here.",
        "I am conserving dramatic reactions.",
        "Still operational. Against the odds.",
        "The cursor knows what it did.",
        "I have seen no compelling evidence.",
        "Current status: present.",
        "I am prepared to look concerned.",
        "The silence has passed inspection.",
        "No news. Efficient.",
        "I continue to occupy several pixels.",
        "The machine hums. I judge quietly.",
        "Nothing urgent has introduced itself.",
        "I remain available in principle.",
        "The day proceeds without documentation.",
        "I have filed this moment under ordinary.",
        "The taskbar remains load-bearing.",
        "No movement on the tiny front.",
        "I am practicing responsible idleness.",
        "Everything is where someone left it.",
        "The atmosphere is adequately digital.",
        "I have detected another twenty minutes.",
        "My watch continues to be metaphorical.",
        "The desktop is showing resilience.",
        "No anomalies worth the paperwork.",
        "I stand ready to stand ready.",
        "The bits appear reasonably arranged.",
        "I am keeping expectations at room temperature.",
        "Status unchanged. Consistency achieved.",
        "The lower screen remains secure.",
        "I have considered pacing. Briefly.",
        "Nothing has requested a committee.",
        "Operations continue without applause.",
        "I am observing a strict minimum of fuss.",
        "The system and I have an understanding.",
        "Another interval, competently endured.",
        "No immediate need for heroics.",
        "I remain cautiously unbothered.",
        "The icons are minding their business.",
        "I have not touched anything important.",
        "Everything is under some degree of control.",
        "The screen remains mostly screen.",
        "I am on standby, aesthetically.",
        "No developments. Very streamlined.",
        "The work continues to look like work.",
        "I have achieved stable smallness.",
        "This corner remains defensible.",
        "Nothing dramatic survived review.",
        "I am quietly exceeding zero expectations.",
        "The taskbar and I remain colleagues.",
        "No bugs have confessed.",
        "I have completed another round of waiting.",
        "The current plan is to remain current.",
        "I am maintaining a low operational profile.",
        "The desktop has declined to comment.",
        "Everything appears almost intentional.",
        "I remain alert to plausible events.",
        "No emergencies in my jurisdiction.",
        "The pixels report acceptable morale.",
        "I have witnessed no preventable excitement.",
        "The machine continues its machine impression.",
        "I am available for modest interventions.",
        "The situation has not become a situation.",
        "No action required. My specialty.",
        "I remain stationed near the consequences.",
        "The day is compiling slowly.",
        "Nothing has exceeded nominal weirdness.",
        "I am keeping the floor informed.",
        "Another quiet success goes uncelebrated.",
        "The interface remains interface-shaped.",
        "I have reserved judgment and memory.",
        "No obvious trouble. Subtle trouble pending.",
        "I remain within acceptable levels of awake.",
        "The taskbar appreciates my restraint.",
        "I have checked the horizon. It is a bezel.",
        "Everything is proceeding in a direction.",
        "I am supervising the concept of readiness.",
        "No incident report required. Yet.",
        "The system remains politely complicated.",
        "I continue to provide ambient competence."
    ];

    private readonly System.Windows.Forms.Timer ambientQuipTimer = new();
    private readonly System.Windows.Forms.Timer ambientQuipDismissTimer = new() { Interval = 10_000 };
    private string? ambientQuip;
    private int previousAmbientQuip = -1;

    private void StartAmbientQuips()
    {
        ambientQuipTimer.Tick += (_, _) =>
        {
            ambientQuipTimer.Stop();
            TryShowAmbientQuip();
            ScheduleAmbientQuip();
        };
        ambientQuipDismissTimer.Tick += (_, _) => DismissAmbientQuip();
        ScheduleAmbientQuip();
    }

    private void ScheduleAmbientQuip()
    {
        ambientQuipTimer.Stop();
        if (!sessionSettings.AmbientQuipsEnabled)
        {
            return;
        }
        ambientQuipTimer.Interval = Random.Shared.Next(18, 24) * 60 * 1000;
        ambientQuipTimer.Start();
    }

    private void TryShowAmbientQuip()
    {
        if (!sessionSettings.AmbientQuipsEnabled ||
            !buddyActive || controller is null || !controller.IsVisible ||
            controller.Message is not null || SessionMessage is not null ||
            dragging || releasingDrag)
        {
            return;
        }

        int index;
        do
        {
            index = Random.Shared.Next(AmbientQuips.Length);
        }
        while (index == previousAmbientQuip);

        previousAmbientQuip = index;
        ambientQuip = AmbientQuips[index];
        UpdateBubble();
        ambientQuipDismissTimer.Start();
    }

    private void TriggerAmbientQuip()
    {
        ambientQuipTimer.Stop();
        DismissAmbientQuip();
        TryShowAmbientQuip();
        ScheduleAmbientQuip();
    }

    private void SetAmbientQuipsEnabled(bool enabled)
    {
        if (enabled)
        {
            ScheduleAmbientQuip();
            return;
        }

        ambientQuipTimer.Stop();
        DismissAmbientQuip();
    }

    private bool DismissAmbientQuip()
    {
        if (ambientQuip is null)
        {
            return false;
        }

        ambientQuip = null;
        ambientQuipDismissTimer.Stop();
        UpdateBubble();
        return true;
    }

    private void StopAmbientQuips()
    {
        ambientQuipTimer.Stop();
        ambientQuipDismissTimer.Stop();
        ambientQuipTimer.Dispose();
        ambientQuipDismissTimer.Dispose();
        ambientQuip = null;
    }
}
