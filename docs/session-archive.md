# Historical preparation-reuse experiment

This branch preserves previously uncommitted source changes based on `7114d3f`, including the historical 0.7.31 version edit, the five persistent-form presentation hooks, and a frozen-route preparation-reuse integration fixture. The original source files were copied without changing their contents; hashes were checked during the copy.

This is an archival branch, not the current release. Later releases already contain related presentation fixes. During migration, the Mod and integration fixture built against the installed game assemblies with zero warnings and zero errors. The frozen-route fixture was not run, so this does not establish native behavior or compatibility with the current release. Any further development should compare it with current `main` before porting a change.

Raw combat inputs, known routes, game resources, saves, credentials, and local logs remain outside Git. The archive does not include an installable game package.
