namespace EmbyCredits
{
    public partial class DetectionRule
    {
        public bool? EnablePostCreditsSceneDetection { get; set; }
        public PostCreditsSceneAction? PostCreditsSceneAction { get; set; }
        public int? PostCreditsMinSceneSeconds { get; set; }
    }
}
