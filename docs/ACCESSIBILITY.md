# Windows accessibility contract

The primary supported screenreader is JAWS for Windows 2025; NVDA is the required secondary validation target. ADFi uses native WPF controls and UI Automation. MY ADFi and DISCOVER are the only top-level navigation choices. Every critical workflow must work with Tab/Shift+Tab, arrow keys, Space/Enter and documented access keys without a mouse.

Controls expose concise accessible names, roles, states and values. Status changes use polite live announcements and report completed semantic steps rather than tokens, percentages at high frequency or raw logs. Focus remains stable across asynchronous updates. Timelines and visual scenes always have ordered list/tree alternatives, explicit time fields and Previous/Next actions; no drag gesture or custom canvas is the only representation. Errors identify the affected field or stage and the recovery action. Large text, high contrast, Windows scaling and keyboard focus indicators are mandatory.

Manual release validation records Windows version, application build, JAWS/NVDA versions, speech settings, task script, observed announcement, focus order and result. Automated XAML checks supplement but never replace actual screenreader testing.

