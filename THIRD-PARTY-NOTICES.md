# 第三方组件与图标说明

第三方图标仅用来识别仓库来源，不表示产品获得 GitHub 或 GitLab 背书。其商标仍属于各权利人。SourceIcons.xaml 将以下 SVG 的路径转换为 WPF Geometry，颜色随主题变化；其余图形为本项目绘制。获取日期：2026-10-16；来源及路径复核：2026-10-17。以下第三方许可不替本项目选择对外发行许可证，也不替代各品牌的商标指引。

## 运行时与外部依赖

Windows x64 便携预览版 0.4.0-preview.2 包含 .NET / WPF 10.0.13，来自 NuGet 的 `Microsoft.NETCore.App.Runtime.win-x64`、`Microsoft.WindowsDesktop.App.Runtime.win-x64` 与 `Microsoft.NETCore.App.Host.win-x64`。运行时包提供的原文许可及第三方声明分别保存在包内 `licenses/<package-id>/`；保留全部文件，不将包含的第三方组件一概当作 MIT。

Git 是外部 CLI，不随 ZIP 分发；Windows 的 Segoe UI、Microsoft YaHei UI 和 Segoe MDL2 Assets 仅通过系统字体引用，不复制字体文件。测试使用的 xUnit、Microsoft.NET.Test.Sdk 及其传递依赖不进入产品包。具体版本和核查范围见包内 `DEPENDENCIES.md`（项目源文件为 `doc/依赖与素材核查.md`）。

## 项目图形

`Themes/Controls.xaml` 的 AppIcon，以及 `Themes/SourceIcons.xaml` 的 Git、Server、Unknown 几何图形由本项目绘制。应用图标不使用 GitHub 或 GitLab 的标志。按钮使用系统 Segoe MDL2 Assets 字形或本项目的简单 Path；没有额外打包图片或字体库。exe 和 Setup 的 ICO 从同一 AppIcon 导出，窗口继续使用 WPF DrawingImage。

## 安装工具

Windows Setup 由 [Inno Setup 7.1.0](https://jrsoftware.org/) 生成。Copyright (C) 1997–2026 Jordan Russell；Portions Copyright (C) 2000–2026 Martijn Laan。安装器保留其内置版权信息，编译器不随 RepoTracker 分发。Inno Setup 的许可见[项目原文](https://github.com/jrsoftware/issrc/blob/is-7_1_0/license.txt)。

## GitHub Octicons — mark-github-16

来源：[mark-github-16.svg](https://github.com/primer/octicons/blob/6dbe38e4e9b4987b2c9332d719c57fbbdd083e2d/icons/mark-github-16.svg)，[许可](https://github.com/primer/octicons/blob/6dbe38e4e9b4987b2c9332d719c57fbbdd083e2d/LICENSE)。复核时 XAML Geometry 与该 SVG 的 path 完全一致。

SVG SHA-256：`7421820090B50AC79D7C2BF7C951A433D7098A8A9C474809207BDD45F62D9D46`。

标志使用规范：[GitHub Brand Toolkit](https://brand.github.com/foundations/logo)。MIT 许可和商标使用条件分别适用；本轮没有取得额外品牌授权。

MIT License

Copyright (c) 2026 GitHub Inc.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## GitLab SVGs — tanuki

来源：[tanuki.svg](https://gitlab.com/gitlab-org/gitlab-svgs/-/blob/ce7ebb7ce0b41cde3ac84f28367c591237c6bafd/sprite_icons/tanuki.svg)，[许可](https://gitlab.com/gitlab-org/gitlab-svgs/-/blob/ce7ebb7ce0b41cde3ac84f28367c591237c6bafd/LICENSE)。复核时 XAML Geometry 与该 SVG 的 path 完全一致。

SVG SHA-256：`4A35444184EC72578FDFDA58C3F17449D74091855FE91D3E0C58B52947CD20ED`。

商标规范：[GitLab Trademark Guidelines](https://handbook.gitlab.com/handbook/marketing/brand-experience/trademark-guidelines/)。本轮未将素材许可解释为额外商标授权。

Copyright (c) 2011-present GitLab Inc.

With regard to the GitLab Software:

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.

For all third party components incorporated into the GitLab Software, those
components are licensed under the original license provided by the owner of the
applicable component.
