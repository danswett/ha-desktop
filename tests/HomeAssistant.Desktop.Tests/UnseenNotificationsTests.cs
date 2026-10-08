using HomeAssistant.Desktop.Services;
using Xunit;

namespace HomeAssistant.Desktop.Tests;

public class UnseenNotificationsTests
{
    [Fact]
    public void CountsEachUntaggedNotificationSeparately()
    {
        var unseen = new UnseenNotifications();

        unseen.Add(null);
        unseen.Add(null);
        unseen.Add(string.Empty);

        Assert.Equal(3, unseen.Count);
    }

    [Fact]
    public void CountsARepeatedTagOnlyOnce()
    {
        var unseen = new UnseenNotifications();

        // The doorbell firing four times is still one thing waiting to be looked at.
        unseen.Add("doorbell");
        unseen.Add("doorbell");
        unseen.Add("doorbell");
        unseen.Add("doorbell");

        Assert.Equal(1, unseen.Count);
    }

    [Fact]
    public void TreatsTagsAsCaseInsensitive()
    {
        var unseen = new UnseenNotifications();

        unseen.Add("Doorbell");
        unseen.Add("doorbell");

        Assert.Equal(1, unseen.Count);
    }

    [Fact]
    public void AddsTaggedAndUntaggedTogether()
    {
        var unseen = new UnseenNotifications();

        unseen.Add("doorbell");
        unseen.Add("doorbell");
        unseen.Add(null);
        unseen.Add("washing-machine");

        Assert.Equal(3, unseen.Count);
    }

    [Fact]
    public void RaisesTheEventOnlyWhenTheCountActuallyMoves()
    {
        var unseen = new UnseenNotifications();
        var seen = new List<int>();
        unseen.CountChanged += seen.Add;

        unseen.Add("doorbell");
        unseen.Add("doorbell");
        unseen.Add(null);

        Assert.Equal([1, 2], seen);
    }

    [Fact]
    public void RemovingATagDropsIt()
    {
        var unseen = new UnseenNotifications();
        unseen.Add("doorbell");
        unseen.Add("washing-machine");

        unseen.Remove("doorbell");

        Assert.Equal(1, unseen.Count);
    }

    [Fact]
    public void RemovingATagThatWasNeverThereChangesNothing()
    {
        var unseen = new UnseenNotifications();
        unseen.Add("doorbell");

        var raised = 0;
        unseen.CountChanged += _ => raised++;
        unseen.Remove("never-sent");

        Assert.Equal(1, unseen.Count);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void RemovingWithoutATagTakesOneOffTheUntaggedTally()
    {
        var unseen = new UnseenNotifications();
        unseen.Add(null);
        unseen.Add(null);

        unseen.Remove(null);

        Assert.Equal(1, unseen.Count);
    }

    [Fact]
    public void TheCountNeverGoesBelowZero()
    {
        var unseen = new UnseenNotifications();

        unseen.Remove(null);
        unseen.Remove(null);
        unseen.Remove("doorbell");

        Assert.Equal(0, unseen.Count);
    }

    [Fact]
    public void ClearingEmptiesEverythingAndSaysSoOnce()
    {
        var unseen = new UnseenNotifications();
        unseen.Add("doorbell");
        unseen.Add(null);

        var seen = new List<int>();
        unseen.CountChanged += seen.Add;
        unseen.Clear();

        Assert.Equal(0, unseen.Count);
        Assert.Equal([0], seen);
    }

    [Fact]
    public void ClearingWhenAlreadyEmptySaysNothing()
    {
        var unseen = new UnseenNotifications();
        var raised = 0;
        unseen.CountChanged += _ => raised++;

        unseen.Clear();

        Assert.Equal(0, raised);
    }

    [Fact]
    public void ATagCanComeBackAfterBeingCleared()
    {
        var unseen = new UnseenNotifications();
        unseen.Add("doorbell");
        unseen.Clear();

        unseen.Add("doorbell");

        Assert.Equal(1, unseen.Count);
    }
}
