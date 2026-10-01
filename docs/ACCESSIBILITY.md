# Windows accessibility contract

The primary supported screenreader is JAWS for Windows 2025; NVDA is the required secondary validation target. ADFi uses native WPF controls and UI Automation. MY ADFi and DISCOVER are the only top-level navigation choices. Every critical workflow must work with Tab/Shift+Tab, arrow keys, Space/Enter and documented access keys without a mouse.

Controls expose concise accessible names, roles, states and values. Status changes use polite live announcements and report completed semantic steps rather than tokens, percentages at high frequency or raw logs. Focus remains stable across asynchronous updates. Timelines and visual scenes always have ordered list/tree alternatives, explicit time fields and Previous/Next actions; no drag gesture or custom canvas is the only representation. Errors identify the affected field or stage and the recovery action. Large text, high contrast, Windows scaling and keyboard focus indicators are mandatory.

Manual release validation records Windows version, application build, JAWS/NVDA versions, speech settings, task script, observed announcement, focus order and result. Automated XAML checks supplement but never replace actual screenreader testing.

## Moment-specific video questions

The viewer exposes both `Alt+Q` and a normally focusable **Ask about this moment** button. Activation pauses first and announces the exact checkpoint before moving focus to the edit field. **DICTATE** is exactly one Tab after that field. Submission announces only “Checking this moment”; completion announces only “Answer ready.” The answer is ordinary selectable text with live announcements disabled, so ADFi does not speak a duplicate answer independently of JAWS/NVDA. Playback never resumes automatically. **Resume from HH:MM:SS** is immediate, while `Escape` closes Q&A and returns focus to player controls without asking or resuming.

The current automated checks validate semantic names, native controls, no canvas-only state, Tab adjacency, production flag defaults and orchestration behavior. Hands-on JAWS 2025/NVDA testing—including `Alt+Q` conflicts with WebView2/YouTube—is a production launch gate, not an inferred pass. See [Interactive Video VQA](INTERACTIVE-VIDEO-VQA.md).
