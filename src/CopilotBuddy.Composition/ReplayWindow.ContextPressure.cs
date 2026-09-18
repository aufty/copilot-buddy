using System.Numerics;
using CopilotBuddy.Core;
using Windows.UI.Composition;

namespace CopilotBuddy.Composition;

internal sealed partial class ReplayWindow
{
    private readonly ContextPressure contextPressure = new();
    private ContainerVisual? contextSweat;
    private readonly List<CompositionObject> sweatResources = [];
    private string? SessionMessage => contextPressure.Message ?? attentionQueue.Current?.Message;
    private bool HasSessionAction => contextPressure.Presented is not null || attentionQueue.Current is not null;

    private void OnContextUsageChanged(object? sender, SessionContextUsage usage) =>
        PostSessionUpdate(() => ChangeContextPressure(() => contextPressure.Update(usage)));

    private void ChangeContextPressure(Action change)
    {
        AdvanceModel();
        bool wasHeavy = contextPressure.IsHeavy;
        string? previousMessage = contextPressure.Message;
        change();
        controller!.SetWalkingSpeedMultiplier(contextPressure.WalkSpeedMultiplier);
        if (wasHeavy != contextPressure.IsHeavy)
        {
            UpdateContextSweat();
            if (!dragging && !releasingDrag && controller.Snapshot.State is VisualState.Idle or VisualState.Walking)
            {
                StartPassiveMotion();
            }
        }
        if (previousMessage != contextPressure.Message)
        {
            RefreshAttention();
        }
    }

    private async Task<bool> HandleContextActionAsync()
    {
        if (contextPressure.Presented is { } session)
        {
            await FocusSessionAsync(session.SessionId, () =>
            {
                contextPressure.Acknowledge(session.SessionId);
                RefreshAttention();
            });
            return true;
        }
        if (contextPressure.Reveal())
        {
            RefreshAttention();
            return true;
        }
        return false;
    }

    private void UpdateContextSweat()
    {
        ClearContextSweat();
        if (!contextPressure.IsHeavy || sessionEffects is null)
        {
            return;
        }
        contextSweat = compositor!.CreateContainerVisual();
        sessionEffects.Children.InsertAtTop(contextSweat);
        CompositionColorBrush blue = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 31, 159, 214));
        CompositionColorBrush light = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 153, 239, 255));
        sweatResources.AddRange([blue, light]);
        float pixel = presentation.Sprite.Scale * dpiScale;
        for (int index = 0; index < 3; index++)
        {
            ContainerVisual drop = compositor.CreateContainerVisual();
            drop.Size = new Vector2(3 * pixel, 5 * pixel);
            contextSweat.Children.InsertAtTop(drop);
            sweatResources.Add(drop);
            AddSweatPixel(drop, blue, new Vector2(pixel, 0), new Vector2(pixel, 5 * pixel));
            AddSweatPixel(drop, blue, new Vector2(0, 2 * pixel), new Vector2(3 * pixel, 2 * pixel));
            AddSweatPixel(drop, light, new Vector2(pixel, pixel), new Vector2(pixel, pixel));
            Vector3 start = new(index % 2 == 0 ? (float)spriteWidth * dpiScale + pixel : -4 * pixel,
                (2 + index * 2) * pixel, 0);
            drop.Offset = start;
            if (presentation.Attention.ReducedMotion)
            {
                continue;
            }
            using LinearEasingFunction linear = compositor.CreateLinearEasingFunction();
            using Vector3KeyFrameAnimation fall = compositor.CreateVector3KeyFrameAnimation();
            fall.Duration = TimeSpan.FromSeconds(1.4);
            fall.DelayTime = TimeSpan.FromSeconds(index * 0.45);
            fall.IterationBehavior = AnimationIterationBehavior.Forever;
            fall.InsertKeyFrame(0, start);
            fall.InsertKeyFrame(1, start + new Vector3(0, 5 * pixel, 0), linear);
            drop.StartAnimation(nameof(drop.Offset), fall);
            using ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
            fade.Duration = fall.Duration;
            fade.DelayTime = fall.DelayTime;
            fade.IterationBehavior = AnimationIterationBehavior.Forever;
            fade.InsertKeyFrame(0, 0);
            fade.InsertKeyFrame(0.15f, 1);
            fade.InsertKeyFrame(0.65f, 1);
            fade.InsertKeyFrame(1, 0);
            drop.Opacity = 0;
            drop.StartAnimation(nameof(drop.Opacity), fade);
        }
    }

    private void AddSweatPixel(ContainerVisual parent, CompositionBrush brush, Vector2 offset, Vector2 size)
    {
        SpriteVisual pixel = compositor!.CreateSpriteVisual();
        pixel.Offset = new Vector3(offset, 0);
        pixel.Size = size;
        pixel.Brush = brush;
        parent.Children.InsertAtTop(pixel);
        sweatResources.Add(pixel);
    }

    private void ClearContextSweat()
    {
        if (contextSweat is not null)
        {
            sessionEffects?.Children.Remove(contextSweat);
            contextSweat.Children.RemoveAll();
            foreach (ContainerVisual visual in sweatResources.OfType<ContainerVisual>())
            {
                visual.Children.RemoveAll();
            }
            foreach (CompositionObject resource in sweatResources)
            {
                resource.Dispose();
            }
            contextSweat.Dispose();
            contextSweat = null;
        }
        sweatResources.Clear();
    }
}