using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

internal static class CardSkills
{
    internal static int[] Ids(CharacterCardTemplate? template) => template is null ? [] :
        new[] { template.TacticalSkill, template.PassiveSkill1, template.PassiveSkill2, template.ResistanceSkill }
            .Where(id => id > 0).Distinct().ToArray();

    internal static uint Level(Item item, int id) =>
        Math.Max(1u, item.Skills.FirstOrDefault(s => s.SkillId == (uint)id)?.SkillLevel ?? 1u);

    internal static long FeedPoints(Item item, CharacterCardTemplate? template) =>
        3L + Ids(template).Sum(id => (long)Level(item, id) - 1);

    internal static int Remaining(Item item, CharacterCardTemplate? template) =>
        Ids(template).Sum(id => (int)Math.Max(0L, 4L - Level(item, id)));

    // Original UI exposes a number of skill-level increases, not a selected skill.
    // Local restoration policy: choose uniformly among skills still below level 4.
    internal static void Apply(Item item, CharacterCardTemplate? template, long points)
    {
        var ids = Ids(template);
        points = Math.Min(points, Remaining(item, template));
        while (points-- > 0)
        {
            var available = ids.Where(id => Level(item, id) < 4).ToArray();
            int id = available[Random.Shared.Next(available.Length)];
            var skill = item.Skills.FirstOrDefault(s => s.SkillId == (uint)id);
            if (skill is null)
            {
                skill = new ItemSkill { SkillId = (uint)id, SkillLevel = 1 };
                item.Skills.Add(skill);
            }
            skill.SkillLevel++;
        }
    }
}
