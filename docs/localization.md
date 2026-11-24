# 贡献桌面翻译

每种语言在仓库根目录的 `locales` 中对应一个 UTF-8 JSON 文件。`en.json` 列出了全部翻译条目，`zh-CN.json` 是内置简体中文，两份内置译文覆盖相同的键。

1. 纠正译文时直接编辑对应语言文件。添加语言时，新建以语言标签命名的文件，例如 `fr.json` 或 `pt-BR.json`。
2. 将 `name` 写成该语言自身的名称，例如 `Français`。在 `messages` 下填写已翻译的键。未提供的条目会使用英文。
3. 保留键名和 `{count}`、`{path}` 等占位符。可按译文语序移动占位符。引号、反斜杠和换行使用 JSON 的转义写法。
4. 在仓库根目录运行 `node scripts/check-locales.mjs`，然后提交该语言文件。构建会自动发现新语言。

例如，一个初始的 `fr.json` 可以只包含这一条译文。

```json
{
  "name": "Français",
  "messages": {
    "common.settings": "Paramètres"
  }
}
```

运行便携版后，在「设置 → 通用」选择该语言并保存，检查搜索、设置、空间分析与索引状态中的译文和控件宽度。测试「跟随系统」时，首先匹配 Windows 显示语言的完整标签，再匹配同语言的译文。无匹配项时使用英文。

## Contribute a translation

Add `locales/<language-tag>.json`, or edit the existing file for your language. Use the language's own name in `name`. Copy the message keys you want to translate from `locales/en.json`. Keep named placeholders such as `{count}` intact, and move them as needed for natural phrasing. Missing messages fall back to English.

Run `node scripts/check-locales.mjs` from the repository root and submit the language file in your PR. Builds pick up new language files automatically. To preview the translation in the Windows app, select your language under **Settings → General → Language** and choose **Save and apply**.
