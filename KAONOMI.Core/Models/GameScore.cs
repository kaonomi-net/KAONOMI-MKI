namespace KAONOMI.Core.Models
{
    /// <summary>
    /// The score of a finished game of the chat (<c>/game:solitaire</c>: the morpion solitaire), with the comment of the player.
    /// Never erased (<see cref="ISoftDeletable"/>).
    /// </summary>
    public class GameScore : EntityBase, ISoftDeletable
    {
        /// <summary>The game: "game1" (the morpion solitaire, /game:solitaire; the title kept for the scores already saved).</summary>
        public string game { get; set; } = "game1";
        /// <summary>The score, weighted by the lines and the speed of the moves.</summary>
        public int score { get; set; }
        /// <summary>The lines drawn.</summary>
        public int lines { get; set; }
        /// <summary>The length of the game, in seconds.</summary>
        public double durationSeconds { get; set; }
        /// <summary>"stopped" (the player gave up) or "blocked" (no move was left).</summary>
        public string end { get; set; } = string.Empty;
        public bool isDeleted { get; set; } = false;
    }
}
