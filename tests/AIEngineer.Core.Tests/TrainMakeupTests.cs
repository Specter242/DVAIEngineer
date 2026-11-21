using AIEngineer.Core.Driving;

namespace AIEngineer.Core.Tests;

public class TrainMakeupTests
{
    private static int Riding(params string[] farToNear) =>
        TrainMakeup.RidingWithLoco(farToNear, c => c == "loco" || c == "tender" || c == "caboose");

    [Fact]
    public void ACabooseNextToTheLocoRidesWithIt()
    {
        Assert.Equal(1, Riding("flat", "flat", "caboose"));
    }

    [Fact]
    public void ACabooseBehindASecondUnitRidesToo()
    {
        Assert.Equal(2, Riding("flat", "caboose", "loco"));
    }

    [Fact]
    public void ACabooseAmongTheCarsIsACarLikeTheRest()
    {
        Assert.Equal(0, Riding("caboose", "flat", "flat"));
        Assert.Equal(0, Riding("flat", "caboose", "flat"));
    }

    [Fact]
    public void ALocoAndCabooseAloneAllRide()
    {
        Assert.Equal(2, Riding("caboose", "tender"));
        Assert.Equal(0, Riding());
    }
}
