using MaaFramework.Binding;
using MaaFramework.Binding.Buffers;
using MaaFramework.Binding.Custom;
using MaaKes.Custom;
using Xunit;

namespace MaaKes.Letterbox.Tests;

public partial class LetterboxTests
{
    [Fact]
    public void CustomTaskAffectsFollowingPipelineAndCanRestoreOriginal()
    {
        var fixture = new TaskFixture();
        using (fixture)
        {
            Assert.Equal(MaaJobStatus.Succeeded, fixture.Tasker.AppendTask("Enable").Wait());
            var wrapped = fixture.Tasker.Controller;
            Assert.NotSame(fixture.Original, wrapped);
            Assert.Equal(MaaJobStatus.Succeeded, fixture.Tasker.AppendTask("StartVerify").Wait());
            Assert.Contains((600, 480), fixture.Raw.Points);
            Assert.Empty(fixture.Before.Points);
            Assert.Equal(MaaJobStatus.Succeeded, fixture.Tasker.AppendTask("Enable").Wait());
            Assert.Same(wrapped, fixture.Tasker.Controller);
            Assert.Equal(1, fixture.Created);
            Assert.Equal(MaaJobStatus.Succeeded, fixture.Tasker.AppendTask("Disable").Wait());
            Assert.Same(fixture.Original, fixture.Tasker.Controller);
            Assert.Equal(1, fixture.Raw.DisposeCount);
            Assert.Equal(0, fixture.Before.DisposeCount);
            Assert.Equal(MaaJobStatus.Succeeded, fixture.Original.Screencap().Wait());
            using var image = new MaaImageBuffer();
            Assert.True(fixture.Original.GetCachedImage(image));
            Assert.Equal((1152, 720), (image.Width, image.Height));
        }
        Assert.Equal(1, fixture.Before.DisposeCount);
        Assert.Equal(1, fixture.Raw.DisposeCount);
    }

    [Fact]
    public void InvalidBarsBlockInputAndRecoverBeforeDisabling()
    {
        using var fixture = new TaskFixture();
        fixture.Raw.ValidBars = false;
        Assert.Equal(MaaJobStatus.Failed, fixture.Tasker.AppendTask("Enable").Wait());
        Assert.NotSame(fixture.Original, fixture.Tasker.Controller);
        Assert.Equal(MaaJobStatus.Failed, fixture.Tasker.Controller.TouchDown(0, 100, 100).Wait());
        Assert.Empty(fixture.Before.Points);
        Assert.Empty(fixture.Raw.Points);
        fixture.Raw.ValidBars = true;
        Assert.Equal(MaaJobStatus.Succeeded, fixture.Tasker.AppendTask("Disable").Wait());
        Assert.Same(fixture.Original, fixture.Tasker.Controller);
    }

    [Fact]
    public void InstancesAndControllerLifetimeStayIndependent()
    {
        var first = new TaskFixture();
        var second = new TaskFixture();
        using (first)
        using (second)
        {
            Assert.Equal(MaaJobStatus.Succeeded, first.Tasker.AppendTask("Enable").Wait());
            Assert.Same(second.Original, second.Tasker.Controller);
            Assert.Equal(MaaJobStatus.Succeeded, second.Tasker.AppendTask("Enable").Wait());
            Assert.NotSame(first.Tasker.Controller, second.Tasker.Controller);
            Assert.Equal(MaaJobStatus.Succeeded, first.Tasker.AppendTask("Disable").Wait());
            Assert.NotSame(second.Original, second.Tasker.Controller);
        }
        Assert.Equal(1, first.Before.DisposeCount);
        Assert.Equal(1, first.Raw.DisposeCount);
        Assert.Equal(1, second.Before.DisposeCount);
        Assert.Equal(1, second.Raw.DisposeCount);
    }

    private sealed class VerifyFrame : IMaaCustomRecognition
    {
        public string Name { get; set; } = "VerifyFrame";
        public bool Analyze<T>(T context, in AnalyzeArgs args, in AnalyzeResults results) where T : IMaaContext
        {
            return args.Image.Width == 1280 && args.Image.Height == 720
                && results.Box.TrySetValues(300, 200, 1, 1)
                && results.Detail.TrySetValue("{}");
        }
    }

    private sealed class TaskFixture : IDisposable
    {
        public readonly FakeController Before = new();
        public readonly FakeController Raw = new();
        public readonly MaaCustomController Original;
        public readonly MaaTasker Tasker;
        public int Created;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MaaKesLetterboxTests", Guid.NewGuid().ToString("N"));

        public TaskFixture()
        {
            Directory.CreateDirectory(Path.Combine(_directory, "pipeline"));
            File.WriteAllText(Path.Combine(_directory, "pipeline", "test.json"), """
                {
                  "Enable": {"action": "Custom", "custom_action": "MaaKesLetterbox", "custom_action_param": {"enabled": true}, "timeout": 1000},
                  "Disable": {"action": "Custom", "custom_action": "MaaKesLetterbox", "custom_action_param": {"enabled": false}, "timeout": 1000},
                  "StartVerify": {"next": ["Verify"], "timeout": 1000},
                  "Verify": {"recognition": "Custom", "custom_recognition": "VerifyFrame", "action": "Click", "target": [300,200,1,1]}
                }
                """);
            var resource = new MaaResource(_directory);
            Assert.True(resource.Register(new MaaKesLetterbox(_ =>
            {
                Created++;
                return new LetterboxController(new MaaCustomController(Raw), () => (Raw.Width, Raw.Height));
            })));
            Assert.True(resource.Register(new VerifyFrame()));
            Original = new MaaCustomController(Before);
            Assert.True(Original.SetOption_ScreenshotTargetShortSide(720));
            Tasker = new MaaTasker(Original, resource, DisposeOptions.Controller | DisposeOptions.Resource);
        }

        public void Dispose()
        {
            Tasker.Dispose();
            // Only this fixture's freshly created directory can be removed.
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MaaKesLetterboxTests")) + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(_directory);
            if (target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) Directory.Delete(target, true);
        }
    }
}
