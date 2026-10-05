# Historical search-ordering experiment

This branch preserves previously uncommitted source changes based on `da3ec6a` (0.7.7). It contains the unpublished discrepancy-search experiment, its integration switches, tests, documentation, and small sanitized comparison results. The original source files were copied without changing their contents; hashes were checked during the copy.

This is an archival branch, not the current release. The experiment's recorded checks and limits are described in [search-ordering-experiment.md](search-ordering-experiment.md). Its original game benchmarks were not repeated during repository migration. Porting this experiment to current `main` requires reviewing the intervening search, goal, replay, and execution changes.

Migration check: the 12 discrepancy tests passed again on the copied source, including comparisons against an independent exhaustive oracle. This is a core algorithm check, not a new native combat benchmark.

Raw combat inputs, game resources, saves, credentials, and local logs remain outside Git. The archive does not include an installable game package.
