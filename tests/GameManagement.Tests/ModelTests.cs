using GameManagement.Models;

namespace GameManagement.Tests;

public sealed class ModelTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1073741824, "1 GB")]
    public void 文件大小应使用易读单位(long size, string expected)
    {
        Assert.Equal(expected, SizeFormatter.Format(size));
    }

    [Fact]
    public void 新游戏和版本应生成非空标识()
    {
        var game = new GameItem();
        var version = new GameVersionItem();

        Assert.NotEqual(Guid.Empty, game.Id);
        Assert.NotEqual(Guid.Empty, version.Id);
        Assert.DoesNotContain(game.Id.ToString(), character => character > 127);
    }
}
