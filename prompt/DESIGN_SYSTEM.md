# yxcode 设计系统文档

## 概述
基于 Claude 官网风格的现代化暗黑主题设计系统，专为 AI 代码助手工具打造。

## 设计原则

### 1. 暗黑主题 (OLED)
- 深黑背景，减少眼睛疲劳
- 高对比度文本，确保可读性
- 适合长时间编码使用

### 2. 现代化风格
- 简洁的界面设计
- 流畅的动画过渡
- 精致的视觉细节

### 3. Claude 风格
- 温暖的橙色主题色
- 柔和的光晕效果
- 专业而友好的视觉语言

## 颜色系统

### 背景色
```css
--bg-primary: #0a0a0a      /* 主背景 - 深黑 */
--bg-secondary: #111111    /* 次级背景 - 卡片/面板 */
--bg-tertiary: #1a1a1a     /* 三级背景 - 输入框 */
--bg-elevated: #1f1f1f     /* 悬浮背景 - 模态框 */
--bg-hover: #252525        /* 悬停状态 */
```

### 文本色
```css
--text-primary: #f5f5f5    /* 主文本 - 高对比 */
--text-secondary: #a8a8a8  /* 次级文本 */
--text-tertiary: #737373   /* 三级文本 */
--text-muted: #525252      /* 弱化文本 */
```

### 主题色 (Claude 橙)
```css
--accent-primary: #f97316  /* 主题色 */
--accent-hover: #fb923c    /* 悬停状态 */
--accent-light: #fdba74    /* 浅色变体 */
--accent-subtle: rgba(249, 115, 22, 0.12)  /* 半透明背景 */
--accent-glow: rgba(249, 115, 22, 0.25)    /* 光晕效果 */
```

### 语义色
```css
--success: #10b981         /* 成功 - 绿色 */
--error: #ef4444           /* 错误 - 红色 */
--warning: #f59e0b         /* 警告 - 黄色 */
--info: #3b82f6            /* 信息 - 蓝色 */
```

### 边框色
```css
--border-primary: #262626  /* 主边框 */
--border-secondary: #1f1f1f /* 次级边框 */
--border-subtle: #171717   /* 微妙边框 */
```

## 排版系统

### 字体家族
```css
--font-sans: 'Inter', -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif
--font-mono: 'Cascadia Code', 'Fira Code', 'SF Mono', Consolas, monospace
```

### 字体大小
- 11px - 辅助文本、标签
- 12px - 小文本、提示
- 13px - 正文、按钮
- 14px - 输入框、卡片内容
- 16px - 问题标题
- 18px - 图标按钮
- 20px - Logo、页面标题

### 字重
- 300 - Light (特殊强调)
- 400 - Regular (正文)
- 500 - Medium (次级标题)
- 600 - Semibold (按钮、标签)
- 700 - Bold (主标题、Logo)

## 间距系统

```css
--space-xs: 4px
--space-sm: 8px
--space-md: 12px
--space-lg: 16px
--space-xl: 24px
--space-2xl: 32px
```

## 圆角系统

```css
--radius-sm: 6px    /* 小元素 */
--radius-md: 8px    /* 按钮、输入框 */
--radius-lg: 12px   /* 卡片 */
--radius-xl: 16px   /* 模态框 */
--radius-full: 9999px /* 圆形 */
```

## 阴影系统

```css
--shadow-sm: 0 1px 3px 0 rgba(0, 0, 0, 0.5)
--shadow-md: 0 4px 8px -2px rgba(0, 0, 0, 0.6)
--shadow-lg: 0 12px 24px -4px rgba(0, 0, 0, 0.7)
--shadow-xl: 0 24px 48px -8px rgba(0, 0, 0, 0.8)
--shadow-glow: 0 0 20px var(--accent-glow)
```

## 动画系统

### 过渡时长
```css
--transition-fast: 150ms    /* 快速交互 */
--transition-base: 200ms    /* 标准过渡 */
--transition-slow: 300ms    /* 复杂动画 */
```

### 缓动函数
```css
cubic-bezier(0.4, 0, 0.2, 1)  /* 标准缓动 */
```

### 关键动画
- `fadeIn` - 淡入效果
- `slideUp` - 向上滑入
- `slideDown` - 向下滑入
- `expandDown` - 展开效果
- `pulse` - 脉冲动画
- `blink` - 闪烁效果
- `loadBounce` - 加载弹跳

## 组件规范

### 按钮
- **主要按钮**: 橙色背景 + 白色文字 + 光晕效果
- **次要按钮**: 灰色背景 + 边框
- **危险按钮**: 红色背景 + 白色文字
- **图标按钮**: 透明背景 + 悬停高亮

### 输入框
- 深色背景 (#1a1a1a)
- 1px 边框
- 聚焦时橙色边框 + 光晕
- 圆角 8px

### 卡片
- 次级背景色
- 1px 边框
- 12px 圆角
- 微妙阴影
- 悬停时增强阴影

### 模态框
- 深色半透明遮罩 (75% 黑色)
- 背景模糊效果
- 16px 圆角
- 大阴影
- 滑入动画

## 可访问性

### 对比度
- 正文文本: 4.5:1 (WCAG AA)
- 大文本: 3:1
- 图标: 3:1

### 键盘导航
- 所有交互元素可通过 Tab 访问
- 清晰的焦点指示器
- 支持 Enter/Space 激活

### 动画
- 支持 `prefers-reduced-motion`
- 可禁用所有动画

## 响应式设计

### 断点
- 移动端: < 768px
- 平板: 768px - 1024px
- 桌面: > 1024px

### 移动端适配
- 侧边栏默认收起
- 简化导航
- 触摸友好的按钮尺寸 (最小 44px)

## 性能优化

### CSS
- 使用 CSS 变量实现主题
- 硬件加速动画 (transform, opacity)
- 避免布局抖动

### 字体
- 使用 Google Fonts CDN
- 仅加载必要字重
- `font-display: swap` 优化加载

## 浏览器支持

- Chrome/Edge 90+
- Firefox 88+
- Safari 14+

## 更新日志

### v2.0 (2026-03-16)
- 完全重写为 Claude 风格暗黑主题
- 引入 Inter 字体
- 优化动画和过渡效果
- 增强可访问性
- 改进响应式设计
