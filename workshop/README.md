# 发布资料 · Publishing assets

`description.bbcode.txt` 是 Steam 页面的中英文说明；`cover.svg` 是原创封面源文件，`image.png` 为其渲染版。它们不使用游戏美术。`mod_id.txt` 在首次成功上传后记录公开项目 ID，供后续更新同一条目。

`description.bbcode.txt` contains bilingual Steam text. The original vector cover and its PNG use no game art. `mod_id.txt` records the public item ID after the first upload so updates reuse the same item.

先运行 `scripts/Build.ps1`，再运行 `scripts/PrepareWorkshop.ps1`。后者只准备文件，不连接 Steam、不创建条目。生成的 `dist/Workshop/content` 仅包含教练 DLL 和清单。使用 [Mega Crit 官方上传工具](https://github.com/megacrit/sts2-mod-uploader) 的 `ModUploader.exe upload -w <workspace>` 发布；需要当前 Steam 账户的发布权限。

Build first, then run `scripts/PrepareWorkshop.ps1`. Preparation does not connect to Steam or create an item. Only the coach DLL and manifest are placed in `content`. Publish using the official uploader with the current Steam account. Preserve `mod_id.txt` when updating to avoid duplicate items.
