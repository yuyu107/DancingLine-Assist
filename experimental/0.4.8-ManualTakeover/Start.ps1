$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -Path (Join-Path $PSScriptRoot 'HintMemory.cs')
Add-Type -Path (Join-Path $PSScriptRoot 'AutoPlayer.cs')
[Windows.Forms.Application]::EnableVisualStyles()
$auto=New-Object AutoPlayer
$form=New-Object Windows.Forms.Form
$form.Text='跳舞的线 · 引导线与自动游玩 0.4.8 手动收集接管实验版'
$form.ClientSize=New-Object Drawing.Size(640,455)
$form.StartPosition='CenterScreen';$form.FormBorderStyle='FixedDialog';$form.MaximizeBox=$false
$label=New-Object Windows.Forms.Label
$label.Text="引导线已通过实测；自动游玩仍在实验阶段，尚未验证通关。`r`n先开启免费引导线，进入关卡后先识别，再启动自动游玩并手动开始。"
$label.SetBounds(20,15,600,50);$form.Controls.Add($label)
$state=New-Object Windows.Forms.Label
$state.Text='等待操作';$state.SetBounds(20,295,600,85);$form.Controls.Add($state)
function Add-Button($text,$x,$y,$handler){
 $b=New-Object Windows.Forms.Button;$b.Text=$text;$b.SetBounds($x,$y,185,38);$b.Add_Click($handler);$form.Controls.Add($b)
}
Add-Button '启用免费引导线' 20 75 {try{$state.Text=[HintMemory]::Apply(1)}catch{$state.Text=$_.Exception.GetBaseException().Message}}
Add-Button '禁用引导线许可' 225 75 {try{$state.Text=[HintMemory]::Apply(2)}catch{$state.Text=$_.Exception.GetBaseException().Message}}
Add-Button '恢复引导线原规则' 430 75 {try{$state.Text=[HintMemory]::Apply(0)}catch{$state.Text=$_.Exception.GetBaseException().Message}}
Add-Button '1. 识别当前关卡' 20 130 {try{$state.Text=$auto.Scan()}catch{$state.Text=$_.Exception.GetBaseException().Message}}
Add-Button '2. 启动自动游玩' 225 130 {try{$auto.InputLeadMilliseconds=[int]$leadInput.Value;$auto.SkipStraightLandingMarkers=$landingFilter.Checked;$auto.Start([int]$offset.Value);$state.Text='已准备；切回游戏后手动开始或继续，F8 停止。'}catch{$state.Text=$_.Exception.GetBaseException().Message}}
Add-Button '停止自动游玩（F8）' 430 130 {$auto.Stop();$state.Text=$auto.Status}
$offsetLabel=New-Object Windows.Forms.Label;$offsetLabel.Text='时间窗口偏移（毫秒）';$offsetLabel.SetBounds(20,192,235,30);$form.Controls.Add($offsetLabel)
$offset=New-Object Windows.Forms.NumericUpDown;$offset.Minimum=-500;$offset.Maximum=100;$offset.Increment=5;$offset.Value=-50;$offset.SetBounds(265,190,90,30);$form.Controls.Add($offset)
Add-Button '导出运行日志' 430 185 {
 try{$p=Join-Path $PSScriptRoot ('AutoPlay-test-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.txt');$auto.SaveLog($p);$state.Text='已保存：'+$p}catch{$state.Text=$_.Exception.GetBaseException().Message}
}
$note=New-Object Windows.Forms.Label
$note.Text="F7：手动接管／回主路后恢复；F8：完全停止。`r`n换关卡后需重新识别；关闭本窗口会停止自动按键。"
$note.SetBounds(20,390,600,55);$form.Controls.Add($note)
$landingFilter=New-Object Windows.Forms.CheckBox
$landingFilter.Text='跳过直行落点提示（实验；重新启动自动游玩生效）'
$landingFilter.Checked=$true;$landingFilter.SetBounds(20,225,590,23);$form.Controls.Add($landingFilter)
$leadLabel=New-Object Windows.Forms.Label;$leadLabel.Text='按键提前量（毫秒；严格路段可试 5）';$leadLabel.SetBounds(20,260,330,25);$form.Controls.Add($leadLabel)
$leadInput=New-Object Windows.Forms.NumericUpDown;$leadInput.Minimum=0;$leadInput.Maximum=40;$leadInput.Value=20;$leadInput.SetBounds(365,257,75,28);$form.Controls.Add($leadInput)
$timer=New-Object Windows.Forms.Timer;$timer.Interval=200
$script:wasRunning=$false
$timer.Add_Tick({if($auto.PollSelection()){$state.Text=$auto.Status};if($auto.Running -or $script:wasRunning){$state.Text=$auto.Status};$script:wasRunning=$auto.Running})
$timer.Start()
$form.Add_FormClosing({$timer.Stop();$auto.Dispose()})
try{[void]$form.ShowDialog()}finally{$timer.Dispose();$auto.Dispose()}
