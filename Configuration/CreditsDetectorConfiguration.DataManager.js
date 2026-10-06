define(['loading', 'toast'], function (loading, toast) {
    'use strict';

    const pluginId = "b1a65a73-a620-432a-9f5b-285038031c26";

    function readNumber(view, selector, options) {
        const input = view.querySelector(selector);
        const raw = input ? input.value : '';
        let value = options.int ? Number.parseInt(raw, 10) : Number.parseFloat(raw);
        if (Number.isNaN(value)) value = options.fallback;
        if (options.min !== undefined && value < options.min) value = options.min;
        if (options.max !== undefined && value > options.max) value = options.max;
        if (input && Number.parseFloat(raw) !== value) input.value = value;
        return value;
    }

    function loadData(instance, view) {
        loading.show();
        return ApiClient.getPluginConfiguration(pluginId).then(config => {
            instance.config = config;

            const detectionMode = config.DetectionMode || 'HashOnly';
            view.querySelector('#selectDetectionMode').value = detectionMode;

            const chkEnableTheIntroDB = view.querySelector('#chkEnableTheIntroDB');
            if (chkEnableTheIntroDB) chkEnableTheIntroDB.checked = config.EnableTheIntroDB || false;
            
            view.querySelector('#chkEnableAutoDetection').checked = config.EnableAutoDetection || false;
            view.querySelector('#txtTimestampOffsetSeconds').value = config.TimestampOffsetSeconds || 0;
            view.querySelector('#chkEnableStrmFileProcessing').checked = config.EnableStrmFileProcessing || false;
            view.querySelector('#chkEnableStrmNetworkResilience').checked = config.EnableStrmNetworkResilience !== false;
            view.querySelector('#chkEnableDetailedLogging').checked = config.EnableDetailedLogging || false;
            view.querySelector('#chkEnableLogToFile').checked = config.EnableLogToFile || false;
            view.querySelector('#txtLogFileFolderPath').value = config.LogFileFolderPath || '';
            view.querySelector('#chkScheduledTaskOnlyProcessMissing').checked = config.ScheduledTaskOnlyProcessMissing !== false;
            view.querySelector('#chkManualSkipExistingMarkers').checked = config.ManualSkipExistingMarkers || false;
            view.querySelector('#chkSkipPreviouslyFailedEpisodes').checked = config.SkipPreviouslyFailedEpisodes !== false;
            view.querySelector('#chkIgnoreFailureMarkers').checked = config.IgnoreFailureMarkers || false;

            view.querySelector('#chkPreventConcurrentPluginProcessing').checked = config.PreventConcurrentPluginProcessing !== false;
            view.querySelector('#chkPauseDetectionDuringPlayback').checked = config.PauseDetectionDuringPlayback || false;
            view.querySelector('#chkEnablePostCreditsSceneDetection').checked = config.EnablePostCreditsSceneDetection || false;
            view.querySelector('#selectPostCreditsSceneAction').value = (config.PostCreditsSceneAction === 'MoveMarker' || config.PostCreditsSceneAction === 'AddChapter') ? 'MoveMarker' : 'Flag';
            view.querySelector('#txtPostCreditsMinSceneSeconds').value = config.PostCreditsMinSceneSeconds || 10;
            view.querySelector('#chkLowerProcessPriority').checked = config.LowerProcessPriority || false;
            view.querySelector('#txtCpuUsageLimit').value = config.CpuUsageLimit !== undefined ? config.CpuUsageLimit : 100;
            view.querySelector('#txtCpuThrottleDelayMs').value = config.CpuThrottleDelayMs !== undefined ? config.CpuThrottleDelayMs : 100;
            view.querySelector('#txtFfmpegOperationTimeoutMinutes').value = config.FfmpegOperationTimeoutMinutes || 0;
            view.querySelector('#txtDelayBetweenEpisodesMs').value = config.DelayBetweenEpisodesMs || 0;
            view.querySelector('#txtTempFolderPath').value = config.TempFolderPath || '';

            view.querySelector('#txtOcrEndpoint').value = config.OcrEndpoint || 'http://localhost:8884';
            view.querySelector('#txtOcrDetectionKeywords').value = config.OcrDetectionKeywords || 'associate producer,based on,cast,casting,cinematography,co-producer,composer,costume design,created by,credits,developed by,directed by,director of photography,editing,editor,end credits,ende,executive producer,fim,fin,fine,guest starring,music by,produced by,producer,production company,production design,screenplay,series producer,sound,special thanks,starring,story by,the end,visual effects,written by,끝,終,キャスト,スタッフ,監督,脚本,音楽,製作,制作,プロデューサー,原作,演出,撮影,編集,おわり,提供,協力,出演';
            view.querySelector('#chkOcrEnableEpisodeComparison').checked = config.OcrEnableEpisodeComparison !== false;
            const searchStartUnit = config.OcrSearchStartUnit || 'minutes';
            let searchStartValue;
            if (config.OcrSearchStartValue !== undefined) {
                searchStartValue = config.OcrSearchStartValue;
            } else if (config.OcrMinutesFromEnd > 0) {
                searchStartValue = config.OcrMinutesFromEnd;
            } else {
                searchStartValue = (config.OcrDetectionSearchStart || 0.65) * 100;
            }
            
            view.querySelector('#selectOcrSearchStartUnit').value = searchStartUnit;
            view.querySelector('#txtOcrSearchStartValue').value = searchStartValue;
            updateSearchStartDescription(view, searchStartUnit);
            
            view.querySelector('#txtOcrFrameRate').value = config.OcrFrameRate || 0.5;
            view.querySelector('#txtOcrMinimumMatches').value = config.OcrMinimumMatches || 1;
            view.querySelector('#txtOcrMaxFramesToProcess').value = config.OcrMaxFramesToProcess || 0;
            view.querySelector('#txtOcrMaxAnalysisDuration').value = config.OcrMaxAnalysisDuration || 600;
            view.querySelector('#txtOcrStopSecondsFromEnd').value = config.OcrStopSecondsFromEnd ?? 20;
            view.querySelector('#txtOcrDelayBetweenFramesMs').value = config.OcrDelayBetweenFramesMs || 0;

            view.querySelector('#txtOcrFfmpegThreads').value = config.OcrFfmpegThreads ?? 2;
            view.querySelector('#txtOcrFfmpegFilterThreads').value = config.OcrFfmpegFilterThreads ?? 1;
            view.querySelector('#chkOcrEnableHdrToneMapping').checked = config.OcrEnableHdrToneMapping || false;
            view.querySelector('#chkOcrEnableHardwareAcceleration').checked = config.OcrEnableHardwareAcceleration || false;
            view.querySelector('#selectOcrHardwareAccelerationType').value = config.OcrHardwareAccelerationType || 'none';
            view.querySelector('#txtOcrHardwareDevice').value = config.OcrHardwareDevice || '';
            view.querySelector('#chkOcrUseHardwareOutputFormat').checked = config.OcrUseHardwareOutputFormat !== false;
            view.querySelector('#chkOcrUseHardwareFilters').checked = config.OcrUseHardwareFilters !== false;
            view.querySelector('#chkOcrUseDirectMemoryPipeline').checked = config.OcrUseDirectMemoryPipeline !== false;
            view.querySelector('#txtOcrFfmpegPreInputArgs').value = config.OcrFfmpegPreInputArgs || '';

            view.querySelector('#chkOcrEnableParallelProcessing').checked = config.OcrEnableParallelProcessing || false;
            view.querySelector('#txtOcrParallelBatchSize').value = config.OcrParallelBatchSize || 4;
            view.querySelector('#txtOcrDelayBetweenBatchesMs').value = config.OcrDelayBetweenBatchesMs ?? 200;
            view.querySelector('#chkOcrEnableSmartFrameSkipping').checked = config.OcrEnableSmartFrameSkipping !== false;
            view.querySelector('#txtOcrConsecutiveMatchesForEarlyStop').value = config.OcrConsecutiveMatchesForEarlyStop || 3;

            view.querySelector('#chkOcrEnableConcurrentFiles').checked = config.OcrEnableConcurrentFiles || false;
            view.querySelector('#txtOcrConcurrentFiles').value = config.OcrConcurrentFiles !== undefined ? config.OcrConcurrentFiles : 2;

            

            view.querySelector('#selectOcrEngine').value = config.OcrEngine || 'Tesseract';
            const localTesseractPathEl = view.querySelector('#txtLocalTesseractPath');
            if (localTesseractPathEl) localTesseractPathEl.value = config.LocalTesseractPath || '';
            view.querySelector('#txtOcrLanguages').value = config.OcrLanguages || 'eng+jpn';
            view.querySelector('#txtOcrPageSegmentationMode').value = config.OcrPageSegmentationMode ?? 3;
            view.querySelector('#txtOcrEngineMode').value = config.OcrEngineMode ?? 3;
            view.querySelector('#chkOcrPreserveInterwordSpaces').checked = config.OcrPreserveInterwordSpaces !== false;

            view.querySelector('#chkOcrEnableImagePreprocessing').checked = config.OcrEnableImagePreprocessing || false;
            view.querySelector('#txtOcrContrastEnhancement').value = config.OcrContrastEnhancement || 1.5;
            view.querySelector('#txtOcrBrightnessAdjustment').value = config.OcrBrightnessAdjustment || 0.05;
            view.querySelector('#chkOcrEnableSharpening').checked = config.OcrEnableSharpening || false;
            view.querySelector('#txtOcrSharpenAmount').value = config.OcrSharpenAmount || 1.0;

            view.querySelector('#chkOcrEnableRoiDetection').checked = config.OcrEnableRoiDetection || false;
            view.querySelector('#selectOcrRoiRegion').value = config.OcrRoiRegion || 'full';

            view.querySelector('#chkOcrEnableFuzzyMatching').checked = config.OcrEnableFuzzyMatching || false;
            view.querySelector('#txtOcrFuzzyMatchMaxDistance').value = config.OcrFuzzyMatchMaxDistance || 2;

            view.querySelector('#chkOcrEnableScrollingDetection').checked = config.OcrEnableScrollingDetection || false;
            view.querySelector('#txtOcrScrollingMinFrames').value = config.OcrScrollingMinFrames || 5;
            view.querySelector('#txtOcrScrollingOverlapThreshold').value = config.OcrScrollingOverlapThreshold || 0.3;

            view.querySelector('#chkOcrEnableAdaptiveFrameRate').checked = config.OcrEnableAdaptiveFrameRate || false;
            view.querySelector('#txtOcrAdaptiveFrameRateMin').value = config.OcrAdaptiveFrameRateMin || 0.25;

            view.querySelector('#chkOcrAdaptiveSamplingEnabled').checked = config.OcrAdaptiveSamplingEnabled === true;
            view.querySelector('#txtOcrAdaptiveCoarseIntervalSeconds').value = config.OcrAdaptiveCoarseIntervalSeconds || 5;
            view.querySelector('#txtOcrAdaptiveRefinementRadiusSeconds').value = config.OcrAdaptiveRefinementRadiusSeconds || 10;

            view.querySelector('#chkOcrEnableCreditStructureDetection').checked = config.OcrEnableCreditStructureDetection || false;
            view.querySelector('#txtOcrMinimumStructureLines').value = config.OcrMinimumStructureLines || 4;

            view.querySelector('#chkBackupImportOverwriteExisting').checked = config.BackupImportOverwriteExisting || false;
            view.querySelector('#txtBackupFolderPath').value = config.BackupFolderPath || '';
            view.querySelector('#txtMaxScheduledBackups').value = config.MaxScheduledBackups !== null && config.MaxScheduledBackups !== undefined ? config.MaxScheduledBackups : 10;
            view.querySelector('#chkEnableAutoBackupAfterDetection').checked = config.EnableAutoBackupAfterDetection || false;
            view.querySelector('#chkSkipDetectionIfFileUnchanged').checked = config.SkipDetectionIfFileUnchanged || false;
            view.querySelector('#chkUseEmbeddedChapterMarkersScheduled').checked = config.UseEmbeddedChapterMarkersScheduled || false;
            view.querySelector('#chkUseEmbeddedChapterMarkersManual').checked = config.UseEmbeddedChapterMarkersManual || false;
            view.querySelector('#chkEnableTracerMode').checked = config.EnableTracerMode || false;
            view.querySelector('#txtTracerMaxEntries').value = config.TracerMaxEntries !== null && config.TracerMaxEntries !== undefined ? config.TracerMaxEntries : 500;
            view.querySelector('#chkOnlyProcessNewEpisodes').checked = config.OnlyProcessNewEpisodes || false;
            view.querySelector('#chkEnableAutoRestoreAfterScan').checked = config.EnableAutoRestoreAfterScan || false;

            view.querySelector('#chkEnableScheduledTaskNotifications').checked = config.EnableScheduledTaskNotifications || false;
            view.querySelector('#chkEnableAutoDetectionNotifications').checked = config.EnableAutoDetectionNotifications || false;
            view.querySelector('#chkNotifyOnSuccessOnly').checked = config.NotifyOnSuccessOnly || false;
            view.querySelector('#txtMinimumEpisodesForNotification').value = config.MinimumEpisodesForNotification !== null && config.MinimumEpisodesForNotification !== undefined ? config.MinimumEpisodesForNotification : 1;

            view.querySelector('#chkEnableThumbnailGeneration').checked = config.EnableThumbnailGeneration !== false;

            view.querySelector('#chkChromaprintEnableBlackFrameFallback').checked = config.ChromaprintEnableBlackFrameFallback !== false;
            view.querySelector('#txtChromaprintMinDuration').value = config.ChromaprintMinDuration || 10;
            view.querySelector('#txtChromaprintMaxDuration').value = config.ChromaprintMaxDuration || 300;
            view.querySelector('#txtChromaprintMinConfidence').value = config.ChromaprintMinConfidence || 0.85;
            view.querySelector('#txtChromaprintMinimumScoreFloor').value = config.ChromaprintMinimumScoreFloor !== undefined ? config.ChromaprintMinimumScoreFloor : 0.55;
            view.querySelector('#txtChromaprintParallelSessions').value = config.ChromaprintParallelSessions !== undefined ? config.ChromaprintParallelSessions : 1;
            view.querySelector('#txtChromaprintFfmpegThreads').value = config.ChromaprintFfmpegThreads ?? 2;
            view.querySelector('#txtChromaprintDelayBetweenOperationsMs').value = config.ChromaprintDelayBetweenOperationsMs || 0;

            view.querySelector('#chkEnableAnimeDetection').checked = config.EnableAnimeDetection !== false;
            
            let animeDetectionMethod = config.AnimeDetectionMethod || 'BlackFrame';
            if (config.EnableBlackFrameForAnime !== undefined && !config.AnimeDetectionMethod) {
                animeDetectionMethod = config.EnableBlackFrameForAnime ? 'BlackFrame' : 'Ocr';
            }
            view.querySelector('#selectAnimeDetectionMethod').value = animeDetectionMethod;
            
            view.querySelector('#txtBlackFrameMinCreditsDuration').value = config.BlackFrameMinCreditsDuration !== undefined ? config.BlackFrameMinCreditsDuration : 15;
            view.querySelector('#txtBlackFrameMaxCreditsDuration').value = config.BlackFrameMaxCreditsDuration !== undefined ? config.BlackFrameMaxCreditsDuration : 450;
            view.querySelector('#chkBlackFrameAutoFallbackToAllFrames').checked = config.BlackFrameAutoFallbackToAllFrames !== false;
            
            view.querySelector('#txtBlackFrameParallelSessions').value = config.BlackFrameParallelSessions !== undefined ? config.BlackFrameParallelSessions : 1;
            view.querySelector('#txtBlackFrameFfmpegThreads').value = config.BlackFrameFfmpegThreads ?? 2;

            view.querySelector('#chkEnableVideoValidation').checked = config.EnableVideoValidation || false;
            view.querySelector('#numVideoValidationTimeoutSeconds').value = config.VideoValidationTimeoutSeconds !== undefined ? config.VideoValidationTimeoutSeconds : 10;

            require(['configurationpage?name=CreditsDetectorConfigurationSeriesManager'], (seriesManager) => {
                seriesManager.loadLibraries(view, config);
                seriesManager.loadLibraryFilter(view);
                seriesManager.loadSeriesList(view);
            });

            require(['configurationpage?name=CreditsDetectorConfigurationAutoSkipManager'], (autoSkipManager) => {
                autoSkipManager.loadAutoSkipSeriesList(view, config.AutoSkipExcludedSeriesIds || []);
            });
            
            require(['configurationpage?name=CreditsDetectorConfigurationRulesManager'], (rulesManager) => {
                rulesManager.loadRules(view, config);
                rulesManager.bindRulesEvents(view, instance);
            });
            
            setupUnitChangeListener(view);

            setTimeout(() => {
                const event = new CustomEvent('keywordsLoaded');
                view.dispatchEvent(event);
            }, 100);

            setTimeout(() => {
                const event = new CustomEvent('detectionMethodLoaded');
                view.dispatchEvent(event);
            }, 100);

            return ApiClient.getJSON(ApiClient.getUrl('CreditsDetector/GetProgress')).catch(err => {
                console.warn('Progress endpoint not available:', err);
                return { IsRunning: false };
            });
        }).then(progress => {
            if (progress && progress.IsRunning) {
                view.querySelector('#progressContainer').style.display = 'block';
                require(['configurationpage?name=CreditsDetectorConfigurationProgressMonitor'], (progressMonitor) => {
                    progressMonitor.startProgressPolling(instance, view);
                });
            }
            
            const selectOcrEngine = view.querySelector('#selectOcrEngine');
            if (selectOcrEngine) {
                const selectedEngine = selectOcrEngine.value;
                const lblOcrEndpoint = view.querySelector('#lblOcrEndpoint');
                const descOcrEndpoint = view.querySelector('#descOcrEndpoint');
                const ocrEndpointContainer = view.querySelector('#ocrEndpointContainer');
                const localTesseractPathContainer = view.querySelector('#localTesseractPathContainer');
                const isLocal = selectedEngine === 'LocalTesseract';

                if (ocrEndpointContainer) ocrEndpointContainer.style.display = isLocal ? 'none' : '';
                if (localTesseractPathContainer) localTesseractPathContainer.style.display = isLocal ? '' : 'none';

                if (selectedEngine === 'PaddleOCR') {
                    if (lblOcrEndpoint) lblOcrEndpoint.textContent = 'PaddleOCR API Endpoint';
                    if (descOcrEndpoint) descOcrEndpoint.innerHTML = 'URL of your PaddleOCR API (e.g., <code>http://localhost:8866</code> or <code>http://192.168.1.100:8866</code>)';
                } else if (!isLocal) {
                    if (lblOcrEndpoint) lblOcrEndpoint.textContent = 'Tesseract OCR API Endpoint';
                    if (descOcrEndpoint) descOcrEndpoint.innerHTML = 'URL of your Tesseract OCR API (e.g., <code>http://localhost:8884</code> or <code>http://192.168.1.100:8884</code>)';
                }
            }
            
            loading.hide();
        }).catch(error => {
            loading.hide();
            console.error('Error loading configuration:', error);
            toast({ type: 'error', text: 'Error loading configuration' });
        });
    }

    function saveData(instance, view, successMessage) {
        loading.show();

        instance.config.EnableAutoDetection = view.querySelector('#chkEnableAutoDetection').checked;

        const chkEnableTheIntroDBSave = view.querySelector('#chkEnableTheIntroDB');
        if (chkEnableTheIntroDBSave) instance.config.EnableTheIntroDB = chkEnableTheIntroDBSave.checked;

        instance.config.TimestampOffsetSeconds = readNumber(view, '#txtTimestampOffsetSeconds', { fallback: 0, min: -600, max: 600 });
        instance.config.EnableStrmFileProcessing = view.querySelector('#chkEnableStrmFileProcessing').checked;
        instance.config.EnableStrmNetworkResilience = view.querySelector('#chkEnableStrmNetworkResilience').checked;
        instance.config.EnableDetailedLogging = view.querySelector('#chkEnableDetailedLogging').checked;
        instance.config.EnableLogToFile = view.querySelector('#chkEnableLogToFile').checked;
        instance.config.LogFileFolderPath = view.querySelector('#txtLogFileFolderPath').value || '';
        instance.config.ScheduledTaskOnlyProcessMissing = view.querySelector('#chkScheduledTaskOnlyProcessMissing').checked;
        instance.config.ManualSkipExistingMarkers = view.querySelector('#chkManualSkipExistingMarkers').checked;
        instance.config.SkipPreviouslyFailedEpisodes = view.querySelector('#chkSkipPreviouslyFailedEpisodes').checked;
        instance.config.IgnoreFailureMarkers = view.querySelector('#chkIgnoreFailureMarkers').checked;

        instance.config.PreventConcurrentPluginProcessing = view.querySelector('#chkPreventConcurrentPluginProcessing').checked;
        instance.config.PauseDetectionDuringPlayback = view.querySelector('#chkPauseDetectionDuringPlayback').checked;
        instance.config.EnablePostCreditsSceneDetection = view.querySelector('#chkEnablePostCreditsSceneDetection').checked;
        instance.config.PostCreditsSceneAction = view.querySelector('#selectPostCreditsSceneAction').value || 'Flag';
        instance.config.PostCreditsMinSceneSeconds = readNumber(view, '#txtPostCreditsMinSceneSeconds', { fallback: 10, min: 3, max: 120, int: true });
        instance.config.LowerProcessPriority = view.querySelector('#chkLowerProcessPriority').checked;
        instance.config.ChromaprintLowerProcessPriority = instance.config.LowerProcessPriority;
        instance.config.CpuUsageLimit = readNumber(view, '#txtCpuUsageLimit', { fallback: 100, min: 1, max: 100, int: true });
        instance.config.CpuThrottleDelayMs = readNumber(view, '#txtCpuThrottleDelayMs', { fallback: 100, min: 0, max: 10000, int: true });
        instance.config.FfmpegOperationTimeoutMinutes = readNumber(view, '#txtFfmpegOperationTimeoutMinutes', { fallback: 0, min: -1, max: 240 });
        instance.config.DelayBetweenEpisodesMs = readNumber(view, '#txtDelayBetweenEpisodesMs', { fallback: 0, min: 0, max: 600000, int: true });
        instance.config.TempFolderPath = view.querySelector('#txtTempFolderPath').value || '';

        instance.config.DetectionMode = view.querySelector('#selectDetectionMode').value || 'HashOnly';
        instance.config.OcrEndpoint = view.querySelector('#txtOcrEndpoint').value || 'http://localhost:8884';
        instance.config.OcrDetectionKeywords = view.querySelector('#txtOcrDetectionKeywords').value || 'directed by,produced by,executive producer,written by,cast,credits,fin,ende,終,끝,fim,fine';
        instance.config.OcrEnableEpisodeComparison = view.querySelector('#chkOcrEnableEpisodeComparison').checked;
                instance.config.OcrRetryAttempts = 5;
        instance.config.OcrRetryDelayMs = 2000;
        instance.config.OcrSearchStartUnit = view.querySelector('#selectOcrSearchStartUnit').value || 'minutes';
        instance.config.OcrSearchStartValue = instance.config.OcrSearchStartUnit === 'percentage'
            ? readNumber(view, '#txtOcrSearchStartValue', { fallback: 65, min: 50, max: 95 })
            : readNumber(view, '#txtOcrSearchStartValue', { fallback: 3, min: 0.5, max: 30 });
        
        if (instance.config.OcrSearchStartUnit === 'minutes') {
            instance.config.OcrMinutesFromEnd = instance.config.OcrSearchStartValue;
            instance.config.OcrDetectionSearchStart = 0.65;
        } else {
            instance.config.OcrMinutesFromEnd = 0;
            instance.config.OcrDetectionSearchStart = instance.config.OcrSearchStartValue / 100;
        }
        
        instance.config.OcrFrameRate = readNumber(view, '#txtOcrFrameRate', { fallback: 0.5, min: 0.05, max: 5 });
        instance.config.OcrMinimumMatches = readNumber(view, '#txtOcrMinimumMatches', { fallback: 1, min: 1, max: 20, int: true });
        instance.config.OcrMaxFramesToProcess = readNumber(view, '#txtOcrMaxFramesToProcess', { fallback: 0, min: 0, max: 10000, int: true });
        instance.config.OcrMaxAnalysisDuration = readNumber(view, '#txtOcrMaxAnalysisDuration', { fallback: 600, min: 0, max: 7200 });
        instance.config.OcrStopSecondsFromEnd = readNumber(view, '#txtOcrStopSecondsFromEnd', { fallback: 20, min: 0, max: 600 });
        instance.config.OcrJpegQuality = 92;
        instance.config.OcrMaxResolutionHeight = 1080;
        instance.config.OcrDelayBetweenFramesMs = readNumber(view, '#txtOcrDelayBetweenFramesMs', { fallback: 0, min: 0, max: 10000, int: true });

        instance.config.OcrFfmpegThreads = readNumber(view, '#txtOcrFfmpegThreads', { fallback: 2, min: 0, max: 64, int: true });
        instance.config.OcrFfmpegFilterThreads = readNumber(view, '#txtOcrFfmpegFilterThreads', { fallback: 1, min: 0, max: 64, int: true });
        instance.config.OcrEnableHdrToneMapping = view.querySelector('#chkOcrEnableHdrToneMapping').checked;
        instance.config.OcrEnableHardwareAcceleration = view.querySelector('#chkOcrEnableHardwareAcceleration').checked;
        instance.config.OcrHardwareAccelerationType = view.querySelector('#selectOcrHardwareAccelerationType').value || 'none';
        instance.config.OcrHardwareDevice = view.querySelector('#txtOcrHardwareDevice').value || '';
        instance.config.OcrUseHardwareOutputFormat = view.querySelector('#chkOcrUseHardwareOutputFormat').checked;
        instance.config.OcrUseHardwareFilters = view.querySelector('#chkOcrUseHardwareFilters').checked;
        instance.config.OcrUseDirectMemoryPipeline = view.querySelector('#chkOcrUseDirectMemoryPipeline').checked;
        instance.config.OcrFfmpegPreInputArgs = view.querySelector('#txtOcrFfmpegPreInputArgs').value || '';

        instance.config.OcrEnableParallelProcessing = view.querySelector('#chkOcrEnableParallelProcessing').checked;
        instance.config.OcrParallelBatchSize = readNumber(view, '#txtOcrParallelBatchSize', { fallback: 4, min: 1, max: 32, int: true });
        instance.config.OcrDelayBetweenBatchesMs = readNumber(view, '#txtOcrDelayBetweenBatchesMs', { fallback: 200, min: 0, max: 10000, int: true });
        instance.config.OcrEnableSmartFrameSkipping = view.querySelector('#chkOcrEnableSmartFrameSkipping').checked;
        instance.config.OcrConsecutiveMatchesForEarlyStop = readNumber(view, '#txtOcrConsecutiveMatchesForEarlyStop', { fallback: 3, min: 0, max: 50, int: true });

        instance.config.OcrEnableConcurrentFiles = view.querySelector('#chkOcrEnableConcurrentFiles').checked;
        instance.config.OcrConcurrentFiles = readNumber(view, '#txtOcrConcurrentFiles', { fallback: 2, min: 1, max: 16, int: true });

        

        instance.config.OcrEngine = view.querySelector('#selectOcrEngine').value || 'Tesseract';
        const saveLocalPathEl = view.querySelector('#txtLocalTesseractPath');
        const rawLocalPath = saveLocalPathEl ? saveLocalPathEl.value || '' : '';
        const rawLocalPathLower = rawLocalPath.trim().toLowerCase();
        if (rawLocalPathLower === 'joshua') {
            if (saveLocalPathEl) saveLocalPathEl.value = 'Greetings professor Falken';
            instance.config.LocalTesseractPath = 'Greetings professor Falken';
        } else if (rawLocalPathLower === 'neminem') {
            if (saveLocalPathEl) saveLocalPathEl.value = 'Mester testeren';
            instance.config.LocalTesseractPath = 'Mester testeren';
        } else {
            instance.config.LocalTesseractPath = rawLocalPath;
        }
        instance.config.OcrLanguages = view.querySelector('#txtOcrLanguages').value || 'eng+jpn';
        instance.config.OcrPageSegmentationMode = Number.parseInt(view.querySelector('#txtOcrPageSegmentationMode').value, 10) ?? 3;
        instance.config.OcrEngineMode = Number.parseInt(view.querySelector('#txtOcrEngineMode').value, 10) ?? 3;
        instance.config.OcrPreserveInterwordSpaces = view.querySelector('#chkOcrPreserveInterwordSpaces').checked;

        instance.config.OcrEnableImagePreprocessing = view.querySelector('#chkOcrEnableImagePreprocessing').checked;
        instance.config.OcrContrastEnhancement = Number.parseFloat(view.querySelector('#txtOcrContrastEnhancement').value) || 1.5;
        instance.config.OcrBrightnessAdjustment = Number.parseFloat(view.querySelector('#txtOcrBrightnessAdjustment').value) || 0.05;
        instance.config.OcrEnableSharpening = view.querySelector('#chkOcrEnableSharpening').checked;
        instance.config.OcrSharpenAmount = Number.parseFloat(view.querySelector('#txtOcrSharpenAmount').value) || 1.0;

        instance.config.OcrEnableRoiDetection = view.querySelector('#chkOcrEnableRoiDetection').checked;
        instance.config.OcrRoiRegion = view.querySelector('#selectOcrRoiRegion').value || 'full';

        instance.config.OcrEnableFuzzyMatching = view.querySelector('#chkOcrEnableFuzzyMatching').checked;
        instance.config.OcrFuzzyMatchMaxDistance = Number.parseInt(view.querySelector('#txtOcrFuzzyMatchMaxDistance').value, 10) || 2;

        instance.config.OcrEnableScrollingDetection = view.querySelector('#chkOcrEnableScrollingDetection').checked;
        instance.config.OcrScrollingMinFrames = Number.parseInt(view.querySelector('#txtOcrScrollingMinFrames').value, 10) || 5;
        instance.config.OcrScrollingOverlapThreshold = Number.parseFloat(view.querySelector('#txtOcrScrollingOverlapThreshold').value) || 0.3;

        instance.config.OcrEnableAdaptiveFrameRate = view.querySelector('#chkOcrEnableAdaptiveFrameRate').checked;
        instance.config.OcrAdaptiveFrameRateMin = Number.parseFloat(view.querySelector('#txtOcrAdaptiveFrameRateMin').value) || 0.25;

        instance.config.OcrAdaptiveSamplingEnabled = view.querySelector('#chkOcrAdaptiveSamplingEnabled').checked;
        instance.config.OcrAdaptiveCoarseIntervalSeconds = Number.parseFloat(view.querySelector('#txtOcrAdaptiveCoarseIntervalSeconds').value) || 5;
        instance.config.OcrAdaptiveRefinementRadiusSeconds = Number.parseFloat(view.querySelector('#txtOcrAdaptiveRefinementRadiusSeconds').value) || 10;

        instance.config.OcrEnableCreditStructureDetection = view.querySelector('#chkOcrEnableCreditStructureDetection').checked;
        instance.config.OcrMinimumStructureLines = Number.parseInt(view.querySelector('#txtOcrMinimumStructureLines').value, 10) || 4;

        instance.config.BackupImportOverwriteExisting = view.querySelector('#chkBackupImportOverwriteExisting').checked;
        instance.config.BackupFolderPath = view.querySelector('#txtBackupFolderPath').value || '';
        instance.config.MaxScheduledBackups = readNumber(view, '#txtMaxScheduledBackups', { fallback: 10, min: 1, max: 100, int: true });
        instance.config.EnableAutoBackupAfterDetection = view.querySelector('#chkEnableAutoBackupAfterDetection').checked;
        instance.config.SkipDetectionIfFileUnchanged = view.querySelector('#chkSkipDetectionIfFileUnchanged').checked;
        instance.config.UseEmbeddedChapterMarkersScheduled = view.querySelector('#chkUseEmbeddedChapterMarkersScheduled').checked;
        instance.config.UseEmbeddedChapterMarkersManual = view.querySelector('#chkUseEmbeddedChapterMarkersManual').checked;
        instance.config.EnableTracerMode = view.querySelector('#chkEnableTracerMode').checked;
        const tracerMaxEntries = Number.parseInt(view.querySelector('#txtTracerMaxEntries').value, 10);
        instance.config.TracerMaxEntries = Number.isNaN(tracerMaxEntries) || tracerMaxEntries < 1 ? 500 : tracerMaxEntries;
        instance.config.OnlyProcessNewEpisodes = view.querySelector('#chkOnlyProcessNewEpisodes').checked;
        instance.config.EnableAutoRestoreAfterScan = view.querySelector('#chkEnableAutoRestoreAfterScan').checked;

        instance.config.EnableScheduledTaskNotifications = view.querySelector('#chkEnableScheduledTaskNotifications').checked;
        instance.config.EnableAutoDetectionNotifications = view.querySelector('#chkEnableAutoDetectionNotifications').checked;
        instance.config.NotifyOnSuccessOnly = view.querySelector('#chkNotifyOnSuccessOnly').checked;
        const minEpisodes = Number.parseInt(view.querySelector('#txtMinimumEpisodesForNotification').value, 10);
        instance.config.MinimumEpisodesForNotification = Number.isNaN(minEpisodes) ? 1 : minEpisodes;

        instance.config.EnableThumbnailGeneration = view.querySelector('#chkEnableThumbnailGeneration').checked;

        instance.config.EnableChromaprintDetection = instance.config.EnableHashDetection;
        instance.config.ChromaprintEnableBlackFrameFallback = view.querySelector('#chkChromaprintEnableBlackFrameFallback').checked;
        instance.config.ChromaprintMinDuration = readNumber(view, '#txtChromaprintMinDuration', { fallback: 10, min: 1, max: 600, int: true });
        instance.config.ChromaprintMaxDuration = readNumber(view, '#txtChromaprintMaxDuration', { fallback: 300, min: 10, max: 1800, int: true });
        instance.config.ChromaprintMinConfidence = readNumber(view, '#txtChromaprintMinConfidence', { fallback: 0.85, min: 0, max: 1 });
        instance.config.ChromaprintMinimumScoreFloor = readNumber(view, '#txtChromaprintMinimumScoreFloor', { fallback: 0.55, min: 0.5, max: 0.85 });
        instance.config.ChromaprintParallelSessions = readNumber(view, '#txtChromaprintParallelSessions', { fallback: 1, min: 1, max: 16, int: true });
        instance.config.ChromaprintFfmpegThreads = readNumber(view, '#txtChromaprintFfmpegThreads', { fallback: 2, min: 0, max: 64, int: true });
        instance.config.ChromaprintDelayBetweenOperationsMs = readNumber(view, '#txtChromaprintDelayBetweenOperationsMs', { fallback: 0, min: 0, max: 60000, int: true });

        instance.config.EnableAnimeDetection = view.querySelector('#chkEnableAnimeDetection').checked;
        instance.config.AnimeDetectionMethod = view.querySelector('#selectAnimeDetectionMethod').value || 'BlackFrame';
        instance.config.BlackFrameMinCreditsDuration = readNumber(view, '#txtBlackFrameMinCreditsDuration', { fallback: 15, min: 1, max: 600 });
        instance.config.BlackFrameMaxCreditsDuration = readNumber(view, '#txtBlackFrameMaxCreditsDuration', { fallback: 450, min: 10, max: 3600 });
        instance.config.BlackFrameAutoFallbackToAllFrames = view.querySelector('#chkBlackFrameAutoFallbackToAllFrames').checked;
        instance.config.BlackFrameParallelSessions = readNumber(view, '#txtBlackFrameParallelSessions', { fallback: 1, min: 1, max: 16, int: true });
        instance.config.BlackFrameFfmpegThreads = readNumber(view, '#txtBlackFrameFfmpegThreads', { fallback: 2, min: 0, max: 64, int: true });
        instance.config.EnableVideoValidation = view.querySelector('#chkEnableVideoValidation').checked;
        instance.config.VideoValidationTimeoutSeconds = readNumber(view, '#numVideoValidationTimeoutSeconds', { fallback: 10, min: 1, max: 60, int: true });

        const durationWarnings = [];
        if (instance.config.ChromaprintMinDuration >= instance.config.ChromaprintMaxDuration) {
            durationWarnings.push('Hash detection');
        }
        if (instance.config.BlackFrameMinCreditsDuration >= instance.config.BlackFrameMaxCreditsDuration) {
            durationWarnings.push('Black Frame detection');
        }
        if (durationWarnings.length > 0) {
            toast({ type: 'warning', text: 'Minimum Credits Duration should be lower than Maximum Credits Duration (' + durationWarnings.join(', ') + ').' });
        }

        const checkboxes = view.querySelectorAll('.chkLibrary');
        if (checkboxes.length > 0) {
            const selectedLibraryIds = [];
            checkboxes.forEach(checkbox => {
                if (checkbox.checked) {
                    selectedLibraryIds.push(checkbox.getAttribute('data-library-id'));
                }
            });
            instance.config.LibraryIds = selectedLibraryIds.length === checkboxes.length ? [] : selectedLibraryIds;
        }

        const autoSkipCheckboxes = view.querySelectorAll('.chkAutoSkipSeries');
        if (autoSkipCheckboxes.length > 0) {
            const excludedIds = [];
            autoSkipCheckboxes.forEach(cb => {
                if (cb.checked) {
                    excludedIds.push(cb.getAttribute('data-series-id'));
                }
            });
            instance.config.AutoSkipExcludedSeriesIds = excludedIds;
        }

        require(['configurationpage?name=CreditsDetectorConfigurationRulesManager'], (rulesManager) => {
            rulesManager.saveRules(instance.config);
            
            ApiClient.updatePluginConfiguration(pluginId, instance.config).then(() => {
                loading.hide();
                toast(successMessage || 'Settings saved');
            }).catch(error => {
                loading.hide();
                console.error('Error saving configuration:', error);
                toast({ type: 'error', text: 'Error saving configuration' });
            });
        });
    }

    const defaultOcrKeywords = 'associate producer,based on,cast,casting,cinematography,co-producer,composer,costume design,created by,credits,developed by,directed by,director of photography,editing,editor,end credits,ende,executive producer,fim,fin,fine,guest starring,music by,produced by,producer,production company,production design,screenplay,series producer,sound,special thanks,starring,story by,the end,visual effects,written by,끝,終,キャスト,スタッフ,監督,脚本,音楽,製作,制作,プロデューサー,原作,演出,撮影,編集,おわり,提供,協力,出演';

    const detectionTuningDefaults = {
        BlackFrameMinimumPercentage: 85,
        BlackFrameThreshold: 28,
        BlackFrameMinDuration: 0.5,
        BlackFrameMinimumDensity: 0.50,
        BlackFrameMaxSceneMergeGap: 20.0,
        BlackFrameScanAllFrames: false,
        BlackFrameRefineCreditsBoundary: true,

        ChromaprintUseAudioFingerprinting: false,
        ChromaprintFingerprintDuration: 360,
        ChromaprintFingerprintSimilarityThreshold: 0.90,
        ChromaprintEnableEpisodeComparison: true,
        ChromaprintEpisodeComparisonTolerance: 15.0,
        ChromaprintEpisodeComparisonMinimumEpisodes: 4,
        ChromaprintAnalysisPercent: 10.0,
        ChromaprintBlackFrameThreshold: 0.05,
        ChromaprintBlackFrameMinDuration: 0.5,
        ChromaprintUseSilenceDetection: true,
        ChromaprintSilenceThreshold: -50,
        ChromaprintSilenceMinDuration: 0.5,
        ChromaprintSilenceSearchWindow: 30.0,
        ChromaprintStopSecondsFromEnd: 20.0,
        ChromaprintUseMultiPeerConsensus: true,
        ChromaprintConsensusPeerCount: 3,
        ChromaprintRejectRepeatingSegments: true,
        ChromaprintRepeatingSegmentThreshold: 0.75,
        ChromaprintRepeatingSegmentCheckSeconds: 90.0,
        ChromaprintUseBoundarySearch: true,
        ChromaprintUseSubFrameInterpolation: true,

        OcrEpisodeComparisonTolerance: 20.0,
        OcrEpisodeComparisonMinimumEpisodes: 4,
        OcrMinimumConfidence: 0.0,
        OcrEnableCharacterDensityDetection: true,
        OcrCharacterDensityThreshold: 20,
        OcrCharacterDensityConsecutiveFrames: 3,
        OcrCharacterDensityPrimaryMethod: true,
        OcrDensityRequireKeyword: true,
        OcrDensityKeywordWindowSeconds: 10.0,
        OcrDensityRequireTemporalConsistency: true,
        OcrDensityMinimumDurationSeconds: 15.0,
        OcrDensityRequireStyleConsistency: true,
        OcrDensityStyleConsistencyThreshold: 0.7
    };

    function resetToDefaults(instance, view) {
        if (!confirm('Reset all settings to their defaults? Folder paths, OCR engine/endpoint, hardware acceleration and library selection are kept.')) {
            return;
        }

        const setIfExists = (selector, value, isCheckbox = false) => {
            const elem = view.querySelector(selector);
            if (elem) {
                if (isCheckbox) elem.checked = value;
                else elem.value = value;
            }
        };

        setIfExists('#selectDetectionMode', 'HashOnly');
        setIfExists('#chkEnableAutoDetection', true, true);
        setIfExists('#chkEnableTheIntroDB', false, true);

        setIfExists('#txtTimestampOffsetSeconds', 0);
        setIfExists('#chkEnableStrmFileProcessing', false, true);
        setIfExists('#chkEnableStrmNetworkResilience', true, true);
        setIfExists('#txtMaxScheduledBackups', 10);
        setIfExists('#chkEnableVideoValidation', false, true);
        setIfExists('#numVideoValidationTimeoutSeconds', 10);
        setIfExists('#chkEnableDetailedLogging', false, true);
        setIfExists('#chkEnableLogToFile', false, true);

        setIfExists('#chkEnableAutoBackupAfterDetection', false, true);
        setIfExists('#chkEnableAutoRestoreAfterScan', false, true);
        setIfExists('#chkUseEmbeddedChapterMarkersScheduled', false, true);
        setIfExists('#chkSkipDetectionIfFileUnchanged', false, true);
        setIfExists('#chkOnlyProcessNewEpisodes', false, true);
        setIfExists('#chkScheduledTaskOnlyProcessMissing', true, true);
        setIfExists('#chkSkipPreviouslyFailedEpisodes', true, true);
        setIfExists('#chkUseEmbeddedChapterMarkersManual', false, true);

        setIfExists('#chkPreventConcurrentPluginProcessing', true, true);
        setIfExists('#chkPauseDetectionDuringPlayback', false, true);
        setIfExists('#chkEnablePostCreditsSceneDetection', false, true);
        setIfExists('#selectPostCreditsSceneAction', 'Flag');
        setIfExists('#txtPostCreditsMinSceneSeconds', 10);
        setIfExists('#chkLowerProcessPriority', true, true);
        setIfExists('#txtCpuUsageLimit', 100);
        setIfExists('#txtCpuThrottleDelayMs', 100);
        setIfExists('#txtDelayBetweenEpisodesMs', 0);
        setIfExists('#txtOcrDelayBetweenFramesMs', 0);
        setIfExists('#txtFfmpegOperationTimeoutMinutes', 0);

        setIfExists('#txtChromaprintParallelSessions', 1);
        setIfExists('#txtChromaprintFfmpegThreads', 2);
        setIfExists('#txtChromaprintDelayBetweenOperationsMs', 0);
        setIfExists('#chkChromaprintEnableBlackFrameFallback', true, true);
        setIfExists('#txtChromaprintMinDuration', 10);
        setIfExists('#txtChromaprintMaxDuration', 300);
        setIfExists('#txtChromaprintMinimumScoreFloor', 0.55);
        setIfExists('#txtChromaprintMinConfidence', 0.85);

        setIfExists('#txtBlackFrameMinCreditsDuration', 15);
        setIfExists('#txtBlackFrameMaxCreditsDuration', 450);
        setIfExists('#chkBlackFrameAutoFallbackToAllFrames', true, true);

        setIfExists('#txtBlackFrameParallelSessions', 1);
        setIfExists('#txtBlackFrameFfmpegThreads', 2);

        setIfExists('#txtOcrFfmpegThreads', 2);
        setIfExists('#txtOcrFfmpegFilterThreads', 1);
        setIfExists('#chkOcrUseHardwareOutputFormat', true, true);
        setIfExists('#chkOcrEnableHdrToneMapping', false, true);
        setIfExists('#chkOcrUseHardwareFilters', true, true);
        setIfExists('#chkOcrUseDirectMemoryPipeline', true, true);
        setIfExists('#txtOcrFfmpegPreInputArgs', '');
        setIfExists('#chkOcrEnableParallelProcessing', false, true);
        setIfExists('#txtOcrParallelBatchSize', 4);
        setIfExists('#txtOcrDelayBetweenBatchesMs', 200);
        setIfExists('#chkOcrEnableSmartFrameSkipping', true, true);
        setIfExists('#txtOcrConsecutiveMatchesForEarlyStop', 3);
        setIfExists('#chkOcrEnableConcurrentFiles', false, true);
        setIfExists('#txtOcrConcurrentFiles', 2);

        setIfExists('#selectOcrSearchStartUnit', 'minutes');
        setIfExists('#txtOcrSearchStartValue', 3);
        updateSearchStartDescription(view, 'minutes');
        setIfExists('#chkOcrEnableEpisodeComparison', true, true);
        setIfExists('#txtOcrFrameRate', 0.5);
        setIfExists('#txtOcrMinimumMatches', 1);
        setIfExists('#txtOcrMaxFramesToProcess', 0);
        setIfExists('#txtOcrMaxAnalysisDuration', 600);
        setIfExists('#txtOcrStopSecondsFromEnd', 20);
        setIfExists('#txtOcrDetectionKeywords', defaultOcrKeywords);

        setIfExists('#chkEnableAnimeDetection', true, true);
        setIfExists('#selectAnimeDetectionMethod', 'BlackFrame');
        setIfExists('#chkEnableThumbnailGeneration', false, true);

        setIfExists('#txtOcrLanguages', 'eng+jpn');
        setIfExists('#txtOcrPageSegmentationMode', 3);
        setIfExists('#txtOcrEngineMode', 3);
        setIfExists('#chkOcrPreserveInterwordSpaces', true, true);
        setIfExists('#chkOcrEnableImagePreprocessing', false, true);
        setIfExists('#txtOcrContrastEnhancement', 1.5);
        setIfExists('#txtOcrBrightnessAdjustment', 0.05);
        setIfExists('#chkOcrEnableSharpening', false, true);
        setIfExists('#txtOcrSharpenAmount', 1.0);
        setIfExists('#chkOcrEnableRoiDetection', false, true);
        setIfExists('#selectOcrRoiRegion', 'full');
        setIfExists('#chkOcrEnableFuzzyMatching', false, true);
        setIfExists('#txtOcrFuzzyMatchMaxDistance', 2);
        setIfExists('#chkOcrEnableScrollingDetection', false, true);
        setIfExists('#txtOcrScrollingMinFrames', 5);
        setIfExists('#txtOcrScrollingOverlapThreshold', 0.3);
        setIfExists('#chkOcrEnableAdaptiveFrameRate', false, true);
        setIfExists('#txtOcrAdaptiveFrameRateMin', 0.25);
        setIfExists('#chkOcrAdaptiveSamplingEnabled', false, true);
        setIfExists('#txtOcrAdaptiveCoarseIntervalSeconds', 5);
        setIfExists('#txtOcrAdaptiveRefinementRadiusSeconds', 10);
        setIfExists('#chkOcrEnableCreditStructureDetection', false, true);
        setIfExists('#txtOcrMinimumStructureLines', 4);

        setIfExists('#chkEnableScheduledTaskNotifications', false, true);
        setIfExists('#chkEnableAutoDetectionNotifications', false, true);
        setIfExists('#chkNotifyOnSuccessOnly', false, true);
        setIfExists('#txtMinimumEpisodesForNotification', 1);

        Object.keys(detectionTuningDefaults).forEach(key => {
            instance.config[key] = detectionTuningDefaults[key];
        });

        view.dispatchEvent(new CustomEvent('keywordsLoaded'));
        view.dispatchEvent(new CustomEvent('detectionMethodLoaded'));

        saveData(instance, view, 'All settings reset to defaults');
    }

    function browseTempFolder(view) {
        require(['directorybrowser'], function(directoryBrowser) {
            var picker = new directoryBrowser();
            picker.show({
                includeFiles: false,
                callback: function(path) {
                    if (path) {
                        var input = view.querySelector('#txtTempFolderPath');
                        input.value = path;
                        input.dispatchEvent(new Event('change', { bubbles: true }));
                    }
                    picker.close();
                }
            });
        });
    }

    function browseBackupFolder(view) {
        require(['directorybrowser'], function(directoryBrowser) {
            var picker = new directoryBrowser();
            picker.show({
                includeFiles: false,
                callback: function(path) {
                    if (path) {
                        var input = view.querySelector('#txtBackupFolderPath');
                        input.value = path;
                        input.dispatchEvent(new Event('change', { bubbles: true }));
                    }
                    picker.close();
                }
            });
        });
    }

    function browseLogFolder(view) {
        require(['directorybrowser'], function(directoryBrowser) {
            var picker = new directoryBrowser();
            picker.show({
                includeFiles: false,
                callback: function(path) {
                    if (path) {
                        var input = view.querySelector('#txtLogFileFolderPath');
                        input.value = path;
                        input.dispatchEvent(new Event('change', { bubbles: true }));
                    }
                    picker.close();
                }
            });
        });
    }

    function updateSearchStartDescription(view, unit) {
        const descMinutes = view.querySelector('#descMinutes');
        const descPercentage = view.querySelector('#descPercentage');
        const valueInput = view.querySelector('#txtOcrSearchStartValue');
        
        if (unit === 'minutes') {
            descMinutes.style.display = '';
            descPercentage.style.display = 'none';
            valueInput.setAttribute('step', '0.5');
            valueInput.setAttribute('min', '0.5');
            valueInput.setAttribute('max', '30');
        } else {
            descMinutes.style.display = 'none';
            descPercentage.style.display = '';
            valueInput.setAttribute('step', '1');
            valueInput.setAttribute('min', '50');
            valueInput.setAttribute('max', '95');
        }
    }

    function setupUnitChangeListener(view) {
        const unitSelector = view.querySelector('#selectOcrSearchStartUnit');
        const valueInput = view.querySelector('#txtOcrSearchStartValue');
        
        unitSelector.addEventListener('change', function() {
            const newUnit = this.value;
            const currentValue = Number.parseFloat(valueInput.value) || 0;
            
            if (newUnit === 'percentage') {
                if (currentValue < 50) {
                    valueInput.value = 65;
                }
            } else {
                if (currentValue >= 50) {
                    valueInput.value = 3;
                }
            }
            
            updateSearchStartDescription(view, newUnit);
        });
    }


    function saveDetectionSettings(view) {
        loading.show();

        ApiClient.getPluginConfiguration('b1a65a73-a620-432a-9f5b-285038031c26').then(config => {
            config.ManualSkipExistingMarkers = view.querySelector('#chkManualSkipExistingMarkers').checked;
            config.IgnoreFailureMarkers = view.querySelector('#chkIgnoreFailureMarkers').checked;

            ApiClient.updatePluginConfiguration('b1a65a73-a620-432a-9f5b-285038031c26', config).then(() => {
                loading.hide();
            }).catch(error => {
                loading.hide();
                console.error('Error saving settings:', error);
                toast({ type: 'error', text: 'Failed to save settings' });
            });
        }).catch(error => {
            loading.hide();
            console.error('Error loading config:', error);
            toast({ type: 'error', text: 'Failed to load configuration' });
        });
    }

    return {
        loadData: loadData,
        saveData: saveData,
        resetToDefaults: resetToDefaults,
        browseTempFolder: browseTempFolder,
        browseBackupFolder: browseBackupFolder,
        browseLogFolder: browseLogFolder,
        setupUnitChangeListener: setupUnitChangeListener,
        saveDetectionSettings: saveDetectionSettings
    };
});
