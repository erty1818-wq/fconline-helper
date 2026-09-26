using FcHelper.Market;

namespace FcHelper.Tests;

public class PlayerSearchTests
{
    /// <summary>The shapes of the data center's search page (2026-09-26), cut down to one or two items each.</summary>
    private const string Page = """
        <div class="season">
            <input id="season_icontm" type="checkbox" class="season_check season_icontm" data-no="100" >
            <label for="season_icontm" alt="ICON The Moment" title="ICON The Moment">
                <img src="https://ssl.nexon.com/s2/game/fc/online/obt/externalAssets/new/season/icontm.png" alt="ICON The Moment">
            </label>
        </div>
        <li><a href="javascript:;" onclick="DataCenter.SetTeamOption(0);" class="league_item selector_item" data-no="0"><span>리그</span></a></li>
        <li><a href="javascript:;" onclick="DataCenter.SetTeamOption(13);" class="league_item selector_item _13" data-no="13"><span>잉글랜드 프리미어리그</span></a></li>
        <li style="display:none"><a href="#" class="club_item selector_item _1" data-no="13" onclick="DataCenter.SetAbilitySearch('1', 'n4TeamId'); $('._area_year_item').show();"><span>아스널</span></a></li>
        <li><a href="javascript:;" onclick="DataCenter.SetNationOption(2);"><span>유럽</span></a></li>
        <li><a href="#" class="nationality_item selector_item _1" data-no="2" onclick="DataCenter.SetAbilitySearch('1', 'n4NationId');"><span>알바니아</span></a></li>
        <li><a href="#" class="ability_item selector_item tcboosts_feature _40200" data-no="40200" onclick="DataCenter.SetTeamColorId('40200','teamcolorid');"><span>20시즌 울산 (ACL 우승)</span></a></li>
        <li><a href="javascript:;" onclick="DataCenter.SetAbilitySearch('크로스 포쳐', 'strTrait1');"><span>크로스 포쳐</span></a></li>
        <li><a href="javascript:;" onclick="DataCenter.SetAbilitySearch('finishing', 'strAbility1');"><span>골결정력</span></a></li>
        <li><a href="javascript:;" onclick="DataCenter.SetAbilitySearch('sprintspeed', 'strSkill1');"><span>속력</span></a></li>
        """;

    [Fact]
    public void Search_page_options_are_read()
    {
        var o = SearchOptionsParser.Parse(Page);
        var season = Assert.Single(o.Seasons);
        Assert.Equal((100, "ICONTM", "ICON The Moment"), (season.Id, season.Code, season.Name));
        Assert.EndsWith("/season/icontm.png", season.Icon);
        Assert.Equal("13", Assert.Single(o.Leagues).Value);
        Assert.Equal(new SearchClub(1, "아스널", 13), Assert.Single(o.Clubs));
        Assert.Equal(new SearchNation(1, "알바니아", 2), Assert.Single(o.Nations));
        Assert.Equal("유럽", Assert.Single(o.Confederations).Name);
        Assert.Equal("40200", Assert.Single(o.TeamColors).Value);
        Assert.Equal("크로스 포쳐", Assert.Single(o.Traits).Value);
        Assert.Equal("finishing", Assert.Single(o.Abilities).Value);
        Assert.Equal("sprintspeed", Assert.Single(o.Columns).Value);
    }

    [Theory]
    [InlineData("리오넬 메시", "ㅁㅅ", true)]
    [InlineData("크리스티아누 호날두", "ㅋㄹㅅㅌㅇㄴ", true)]
    [InlineData("손흥민", "ㅅㅎㅁ", true)]
    [InlineData("손흥민", "ㅁㅅ", false)]
    public void Initials_match_names(string name, string initials, bool expected)
    {
        Assert.True(Initials.IsInitials(initials));
        Assert.Equal(expected, Initials.Matches(name, initials));
    }

    [Fact]
    public void Whole_letters_are_not_initials() => Assert.False(Initials.IsInitials("메시"));

    [Theory]
    [InlineData("FC 바이에른 뮌헨", "뮌헨")]
    [InlineData("FC 바이에른 뮌헨", "바이에른 뮌헨")]
    [InlineData("26TOTS Team of the Season", "tots")]
    public void Option_search_matches_every_term_anywhere(string name, string query) =>
        Assert.True(OptionSearch.Matches(name, query));

    [Fact]
    public void Option_search_rejects_a_missing_term() =>
        Assert.False(OptionSearch.Matches("TSV 1860 뮌헨", "바이에른 뮌헨"));
}
