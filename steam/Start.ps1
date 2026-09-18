$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$formAssemblies=@([System.Windows.Forms.Form].Assembly.Location,[System.Drawing.Color].Assembly.Location)
Add-Type -Path (Join-Path $PSScriptRoot 'AutoPlayer.cs') -ReferencedAssemblies $formAssemblies
Add-Type -Path (Join-Path $PSScriptRoot 'HintMemory.cs')
[Windows.Forms.Application]::EnableVisualStyles()
$auto=New-Object AutoPlayer
$overlay=New-Object AssistOverlay
$form=New-Object Windows.Forms.Form
$form.Text='跳舞的线 Steam · 引导线与自动游玩 0.3.9 正式版'
$form.ClientSize=New-Object Drawing.Size(640,490)
$form.StartPosition='CenterScreen';$form.FormBorderStyle='FixedDialog';$form.MaximizeBox=$false
$label=New-Object Windows.Forms.Label
$label.Text="Steam 版引导线权限已验证。`r`n引导线可作为可视化辅助；自动游玩直接识别关卡内部引导点，不要求路线显示。"
$label.SetBounds(20,15,600,50);$form.Controls.Add($label)
$state=New-Object Windows.Forms.Label
$state.Text='等待操作';$state.SetBounds(20,345,600,70);$form.Controls.Add($state)
function Add-Button($text,$x,$y,$handler){
 $b=New-Object Windows.Forms.Button;$b.Text=$text;$b.SetBounds($x,$y,185,38);$b.Add_Click($handler);$form.Controls.Add($b)
}
Add-Button '应用引导线设置' 20 75 {try{$mode=1;if($defaultHint.Checked){$mode=2};$state.Text=[HintMemory]::Apply($mode)}catch{$state.Text=$_.Exception.GetBaseException().Message}}
Add-Button '恢复引导线原规则' 225 75 {try{$state.Text=[HintMemory]::Apply(0)}catch{$state.Text=$_.Exception.GetBaseException().Message}}
$defaultHint=New-Object Windows.Forms.CheckBox
$defaultHint.Text='进入关卡时尝试默认开启（可选；若路线未出现，需在游戏内关闭再开启）'
$defaultHint.Checked=$false;$defaultHint.SetBounds(20,112,600,25);$form.Controls.Add($defaultHint)
function Scan-CurrentLevel {try{$auto.BeginScan();$state.Text=$auto.Status}catch{$state.Text=$_.Exception.GetBaseException().Message}}
Add-Button '1. 识别当前关卡（F6）' 20 145 {Scan-CurrentLevel}
function Start-AutoPlay {
 try{
  if($auto.Running){return}
  $auto.InputLeadMilliseconds=[int]$leadInput.Value
  $auto.SkipStraightLandingMarkers=$landingFilter.Checked
  $auto.SkipTerminalStraightMarker=$terminalFilter.Checked
  $auto.Start([int]$offset.Value)
  $state.Text='已准备；切回游戏后手动开始或继续，F8 停止。'
 }catch{$state.Text=$_.Exception.GetBaseException().Message}
}
Add-Button '2. 启动自动游玩（F7）' 225 145 {Start-AutoPlay}
Add-Button '停止自动游玩（F8）' 430 145 {$auto.Stop();$state.Text=$auto.Status}
$offsetLabel=New-Object Windows.Forms.Label;$offsetLabel.Text='时间窗口偏移（毫秒）';$offsetLabel.SetBounds(20,192,235,30);$form.Controls.Add($offsetLabel)
$offset=New-Object Windows.Forms.NumericUpDown;$offset.Minimum=-500;$offset.Maximum=100;$offset.Increment=5;$offset.Value=-50;$offset.SetBounds(265,190,90,30);$form.Controls.Add($offset)
Add-Button '导出运行日志' 430 185 {
 try{$p=Join-Path $PSScriptRoot ('Steam-AutoPlay-test-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.txt');$auto.SaveLog($p);$state.Text='已保存：'+$p}catch{$state.Text=$_.Exception.GetBaseException().Message}
}
$note=New-Object Windows.Forms.Label
$note.Text="F6 识别当前关卡，F7 启动/恢复自动游玩，F8 停止；遮罩只显示工具状态，不修改游戏画面。`r`n独占全屏下遮罩可能不会显示；换关卡后仍需重新识别。"
$note.SetBounds(20,420,600,55);$form.Controls.Add($note)
$landingFilter=New-Object Windows.Forms.CheckBox
$landingFilter.Text='跳过直行落点提示（实验；重新启动自动游玩生效）'
$landingFilter.Checked=$true;$landingFilter.SetBounds(20,225,590,23);$form.Controls.Add($landingFilter)
$terminalFilter=New-Object Windows.Forms.CheckBox
$terminalFilter.Text='跳过最后一个直行终点框（仅确认该框不用点时开启）'
$terminalFilter.Checked=$false;$terminalFilter.SetBounds(20,250,590,23);$form.Controls.Add($terminalFilter)
$overlayToggle=New-Object Windows.Forms.CheckBox
$overlayToggle.Text='在游戏画面显示自动游玩状态遮罩（默认开启）'
$overlayToggle.Checked=$true;$overlayToggle.SetBounds(20,275,590,23);$form.Controls.Add($overlayToggle)
$script:restoringOverlay=$false
$overlayToggle.Add_CheckedChanged({
 if(-not $overlayToggle.Checked -and -not $script:restoringOverlay){
  $message="游戏内遮罩会明确显示自动游玩状态。`r`n`r`n关闭后，如录制或分享自动游玩视频，请自行在画面或说明中明确标注自动游玩，避免被误认为手动操作。`r`n`r`n确定关闭遮罩吗？"
  $reply=[Windows.Forms.MessageBox]::Show($message,'关闭自动游玩状态遮罩',[Windows.Forms.MessageBoxButtons]::YesNo,[Windows.Forms.MessageBoxIcon]::Warning,[Windows.Forms.MessageBoxDefaultButton]::Button2)
  if($reply -ne [Windows.Forms.DialogResult]::Yes){$script:restoringOverlay=$true;$overlayToggle.Checked=$true;$script:restoringOverlay=$false}
 }
})
$leadLabel=New-Object Windows.Forms.Label;$leadLabel.Text='按键提前量（毫秒；严格路段可试 5）';$leadLabel.SetBounds(20,305,330,25);$form.Controls.Add($leadLabel)
$leadInput=New-Object Windows.Forms.NumericUpDown;$leadInput.Minimum=0;$leadInput.Maximum=40;$leadInput.Value=20;$leadInput.SetBounds(365,302,75,28);$form.Controls.Add($leadInput)
$timer=New-Object Windows.Forms.Timer;$timer.Interval=16
$script:wasRunning=$false
$script:gameProc=$null;$script:nextGameProbe=[DateTime]::MinValue;$script:nextStatePoll=[DateTime]::MinValue
$timer.Add_Tick({
 $now=[DateTime]::UtcNow
 $scanHotkey=$auto.ConsumeScanHotkey()
 if($scanHotkey -and -not $auto.Running -and -not $auto.Scanning){Scan-CurrentLevel}
 $startHotkey=$auto.ConsumeStartHotkey()
 if($startHotkey -and -not $auto.Running -and -not $auto.Scanning){Start-AutoPlay}
 if($overlayToggle.Checked){
  if($now -ge $script:nextGameProbe){try{$script:gameProc=Get-Process -Name 'Dancing Line' -ErrorAction Stop | Select-Object -First 1}catch{$script:gameProc=$null};$script:nextGameProbe=$now.AddSeconds(1)}
  if($null -ne $script:gameProc){try{$overlay.FollowGame($script:gameProc.MainWindowHandle,$auto.CurrentPoint,$auto.Count,$auto.Running,$auto.HasExecutedPoint,$auto.InputHeld)}catch{$overlay.Disable()}}
 }else{$overlay.Disable()}
 if($now -ge $script:nextStatePoll){
  if($auto.ConsumeScanResult()){$state.Text=$auto.Status}
  if(-not $auto.Scanning -and $auto.PollSelection()){$state.Text=$auto.Status}
  if($auto.Running -or $script:wasRunning){$state.Text=$auto.Status}
  $script:wasRunning=$auto.Running;$script:nextStatePoll=$now.AddMilliseconds(200)
 }
})
$timer.Start()
$form.Add_Activated({$overlay.Disable()})
$form.Add_FormClosing({$timer.Stop();$overlay.Disable();$overlay.Dispose();$auto.Dispose()})
try{[void]$form.ShowDialog()}finally{$timer.Dispose();$overlay.Dispose();$auto.Dispose()}
