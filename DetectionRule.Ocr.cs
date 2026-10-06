namespace EmbyCredits
{
    public partial class DetectionRule
    {
        public OcrEngine? OcrEngine { get; set; }
        public double? OcrSearchStartValue { get; set; }
        public double? OcrFrameRate { get; set; }
        public int? OcrMaxFramesToProcess { get; set; }
        public double? OcrMaxAnalysisDuration { get; set; }
        public string? OcrDetectionKeywords { get; set; }

        public bool? OcrEnableEpisodeComparison { get; set; }
        public bool? OcrEnableHdrToneMapping { get; set; }

        public string? OcrLanguages { get; set; }
        public int? OcrPageSegmentationMode { get; set; }
        public int? OcrEngineMode { get; set; }
        public bool? OcrPreserveInterwordSpaces { get; set; }
        public bool? OcrEnableSmartFrameSkipping { get; set; }
        public int? OcrConsecutiveMatchesForEarlyStop { get; set; }

        public bool? OcrEnableImagePreprocessing { get; set; }
        public double? OcrContrastEnhancement { get; set; }
        public double? OcrBrightnessAdjustment { get; set; }
        public bool? OcrEnableSharpening { get; set; }
        public double? OcrSharpenAmount { get; set; }

        public bool? OcrEnableRoiDetection { get; set; }
        public string? OcrRoiRegion { get; set; }

        public bool? OcrEnableFuzzyMatching { get; set; }
        public int? OcrFuzzyMatchMaxDistance { get; set; }

        public bool? OcrEnableScrollingDetection { get; set; }
        public int? OcrScrollingMinFrames { get; set; }
        public double? OcrScrollingOverlapThreshold { get; set; }

        public bool? OcrEnableAdaptiveFrameRate { get; set; }
        public double? OcrAdaptiveFrameRateMin { get; set; }
        public bool? OcrAdaptiveSamplingEnabled { get; set; }
        public double? OcrAdaptiveCoarseIntervalSeconds { get; set; }
        public double? OcrAdaptiveRefinementRadiusSeconds { get; set; }

        public bool? OcrEnableCreditStructureDetection { get; set; }
        public int? OcrMinimumStructureLines { get; set; }
    }
}
