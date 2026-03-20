using ChineseChess.Application.Configuration;
using ChineseChess.Application.Enums;
using System.Text.Json;
using Xunit;

namespace ChineseChess.Tests.Application;

/// <summary>
/// PikafishSettings 的預設值、序列化往返與向後相容性測試。
/// </summary>
public class PikafishSettingsTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var settings = new PikafishSettings();

        Assert.Equal(1, settings.MultiPv);
        Assert.Equal(20, settings.SkillLevel);
        Assert.False(settings.UciLimitStrength);
        Assert.Equal(2850, settings.UciElo);
        Assert.True(settings.SixtyMoveRule);
        Assert.Equal(120, settings.Rule60MaxPly);
        Assert.Equal(0, settings.MateThreatDepth);
        Assert.Equal(PikafishScoreType.Elo, settings.ScoreType);
        Assert.True(settings.LuOutput);
        Assert.Equal(PikafishDrawRule.None, settings.DrawRule);
        Assert.Equal(string.Empty, settings.EvalFile);
    }

    [Fact]
    public void Serialize_ThenDeserialize_PreservesPikafishSettings()
    {
        var original = new ExternalEngineSettings
        {
            RedPikafish = new PikafishSettings
            {
                MultiPv          = 4,
                SkillLevel       = 15,
                UciLimitStrength = true,
                UciElo           = 2000,
                SixtyMoveRule    = false,
                Rule60MaxPly     = 60,
                MateThreatDepth  = 3,
                ScoreType        = PikafishScoreType.Raw,
                LuOutput         = false,
                DrawRule         = PikafishDrawRule.DrawAsBlackWin,
                EvalFile         = "custom.nnue"
            }
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<ExternalEngineSettings>(json);

        Assert.NotNull(deserialized);
        var rp = deserialized.RedPikafish;
        Assert.Equal(4, rp.MultiPv);
        Assert.Equal(15, rp.SkillLevel);
        Assert.True(rp.UciLimitStrength);
        Assert.Equal(2000, rp.UciElo);
        Assert.False(rp.SixtyMoveRule);
        Assert.Equal(60, rp.Rule60MaxPly);
        Assert.Equal(3, rp.MateThreatDepth);
        Assert.Equal(PikafishScoreType.Raw, rp.ScoreType);
        Assert.False(rp.LuOutput);
        Assert.Equal(PikafishDrawRule.DrawAsBlackWin, rp.DrawRule);
        Assert.Equal("custom.nnue", rp.EvalFile);
    }

    [Fact]
    public void OldJsonWithoutPikafishFields_DeserializesWithDefaults()
    {
        // 模擬舊版不含 PikafishSettings 欄位的 JSON
        const string oldJson = """
            {
                "UseRedExternalEngine": false,
                "RedEnginePath": "",
                "RedProtocol": 0,
                "UseBlackExternalEngine": false,
                "BlackEnginePath": "",
                "BlackProtocol": 0,
                "ServerPort": 23333
            }
            """;

        var settings = JsonSerializer.Deserialize<ExternalEngineSettings>(oldJson);

        Assert.NotNull(settings);
        // 缺少 PikafishSettings 欄位時應使用預設值
        Assert.NotNull(settings.RedPikafish);
        Assert.NotNull(settings.BlackPikafish);
        Assert.Equal(1, settings.RedPikafish.MultiPv);
        Assert.Equal(20, settings.RedPikafish.SkillLevel);
        Assert.Equal(1, settings.BlackPikafish.MultiPv);
    }
}
