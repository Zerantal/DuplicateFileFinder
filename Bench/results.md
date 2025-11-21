# Scan performance

## Master branch
* **Cold**
* location: /home/z
* Samsung SSD 840 EVO 250GB 

```
2025-11-09 16:14:26.8186|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Preparing""" in 3 ms
2025-11-09 16:14:29.7032|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Enumerating""" in 2,860 ms" files=156550 folders=20796"
2025-11-09 16:14:44.5061|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Hashing""" in 14,803 ms" targets=138538"
2025-11-09 16:14:44.7507|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Grouping""" in 245 ms""
2025-11-09 16:14:44.7512|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Committing""" in 0 ms""
2025-11-09 16:14:44.7940|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "RecomputingAggregates""" in 43 ms""
```

## Master branch
* **Warm**
* location: /home/z
* Samsung SSD 840 EVO 250GB

```
2025-11-09 16:16:06.9511|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Preparing""" in 3 ms
2025-11-09 16:16:07.8451|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Enumerating""" in 870 ms" files=156573 folders=20796"
2025-11-09 16:16:10.8838|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Hashing""" in 3,039 ms" targets=138563"
2025-11-09 16:16:11.1298|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Grouping""" in 246 ms""
2025-11-09 16:16:11.1302|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Committing""" in 0 ms""
2025-11-09 16:16:11.1708|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "RecomputingAggregates""" in 40 ms""
```

## branch - integrate-repo
* date: 19-11-2025
* external nvme ssd

### Cold
```
2025-11-19 14:23:21.6606|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Opening Repo" in 74 ms" dirs=0 files=0 HashIndex=0"
2025-11-19 14:23:21.6900|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Preparing" in 1 ms""
2025-11-19 14:23:30.4856|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Enumerating" in 8,795 ms" files=73976 folders=53814"
2025-11-19 14:23:42.0041|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Hashing" in 11,517 ms" AggregateSize=9.700 GB targets=64027"
2025-11-19 14:23:42.1549|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Grouping" in 151 ms""
2025-11-19 14:23:42.1554|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Committing" in 0 ms""
2025-11-19 14:23:42.2259|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "RecomputingAggregates" in 70 ms""
2025-11-19 14:23:42.2411|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Folder scan" ("/mnt/external_vm_storage/testScanTarget/") in 20,562 ms""
```

### Warm
```
2025-11-19 14:25:31.6651|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Opening Repo" in 69 ms" dirs=0 files=0 HashIndex=0"
2025-11-19 14:25:31.6939|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Preparing" in 1 ms""
2025-11-19 14:25:39.2718|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Enumerating" in 7,578 ms" files=73976 folders=53814"
2025-11-19 14:25:43.1065|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Hashing" in 3,833 ms" AggregateSize=9.700 GB targets=64027"
2025-11-19 14:25:43.2908|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Grouping" in 184 ms""
2025-11-19 14:25:43.2908|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Committing" in 0 ms""
2025-11-19 14:25:43.3500|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "RecomputingAggregates" in 59 ms""
2025-11-19 14:25:43.3782|INFO|DuplicateFileFinderLib.Logging.TimingLog|Completed "Folder scan" ("/mnt/external_vm_storage/testScanTarget/") in 11,695 ms""
```
