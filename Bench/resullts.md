# Scan performance

## Master branch


## New Indexed Db branch (direct scan only)
* **Cold**
* location: /home/z
* Direct scan only (no metadata index Db)
* Samsung SSD 840 EVO 250GB

```
2025-11-09 15:56:32.7094|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Preparing""" in 3 ms
2025-11-09 15:56:35.8652|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Enumerating""" in 3,131 ms" files=156454 folders=20786"
2025-11-09 15:56:50.5649|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Hashing""" in 14,700 ms" targets=138448"
2025-11-09 15:56:50.7850|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Grouping""" in 220 ms""
2025-11-09 15:56:50.7855|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Committing""" in 0 ms""
2025-11-09 15:56:50.8438|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "RecomputingAggregates""" in 58 ms""
```

## New Indexed Db branch (direct scan only)
* **Warm**
* location: /home/z
* Direct scan only (no metadata index Db)
* Samsung SSD 840 EVO 250GB

```
2025-11-09 16:03:43.4381|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Preparing""" in 3 ms
2025-11-09 16:03:44.6071|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Enumerating""" in 1,145 ms" files=156513 folders=20786"
2025-11-09 16:03:47.5848|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Hashing""" in 2,978 ms" targets=138504"
2025-11-09 16:03:47.7939|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Grouping""" in 209 ms""
2025-11-09 16:03:47.7943|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Committing""" in 0 ms""
2025-11-09 16:03:47.8512|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "RecomputingAggregates""" in 57 ms""
```