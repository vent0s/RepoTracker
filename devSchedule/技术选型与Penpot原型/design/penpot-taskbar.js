// Public copy: replace the placeholder file/page IDs with your own target before running.
// Taskbar entry revision, 2026-10-16. Run after prototype, refinements, connect.
// This modifies the design only. It does not pin an app or change Windows.
const R=storage.rt;
if(penpot.currentFile.id!=='00000000-0000-4000-8000-000000000002'||penpot.currentPage.id!=='00000000-0000-4000-8000-000000000003')throw new Error('Wrong target');
if(R.taskbarRevisionComplete)return {alreadyApplied:true};
const find=(b,name)=>penpotUtils.findShape(s=>s.name===name,b);
const nav=(shape,key)=>{for(const i of [...shape.interactions])shape.removeInteraction(i);shape.addInteraction('click',{type:'navigate-to',destination:R.boards[key]});};
const swapText=(b,name,value)=>{const s=find(b,name);if(s)s.characters=value;};
R.iconPaths.minus='M5 12h14';
R.iconPaths.more='M4 12h.1M12 12h.1M20 12h.1';
const block=penpot.history.undoBlockBegin();
try{
 R.boards.main.name='01 / 仓库面板 · 内容视图';
 R.boards.pinned.name='04 / 面板保持展开';
 R.boards.tray.name='19 / 应用菜单';
 for(const b of Object.values(R.boards)){
  for(const t of penpotUtils.findShapes(s=>s.type==='text',b)){
   if(t.characters==='把仓库状态，收进托盘。')t.characters='从任务栏，一眼看清仓库。';
   if(t.characters==='登录 Windows 后在托盘运行。')t.characters='登录 Windows 后启动，并保留任务栏入口。';
   if(t.characters==='已开启。登录 Windows 后在托盘运行。')t.characters='已开启。登录 Windows 后保留任务栏入口。';
   if(t.characters==='实际应用将停止检查并移除托盘图标。')t.characters='实际应用将退出进程；已固定的任务栏快捷方式保留。';
   if(t.characters==='托盘右键菜单')t.characters='应用菜单';
   if(t.characters==='REPOTRACKER  /  WINDOWS TRAY UTILITY')t.characters='REPOTRACKER  /  WINDOWS TASKBAR COMPANION';
   if(t.characters.includes('失焦收起、任务栏锚定由桌面实现验收。'))t.characters='任务栏应用按钮是主入口，点击展开、收起后继续保留。\n面板可保持展开；设置与退出放在应用内菜单。';
  }
 }
 swapText(R.boards.pinned,'Subtitle','保持展开 · 点击收起可返回任务栏');
 swapText(R.boards.guide,'Cover title','任务栏一点，仓库尽览。');
 swapText(R.boards.guide,'Cover description','任务栏直接进入 · 展开即看 · 收起常驻');
 swapText(R.boards.guide,'Principle 2','02  从任务栏直接进入');
 for(const key of ['main','light','pinned','offline','remoteOff','added','removed','firstMain','empty']){
  const b=R.boards[key];const theme=key==='light'?R.light:R.dark;
  for(const [kind,x] of [['refresh',540],['pin',580],['settings',620]]){const s=find(b,'Action / '+kind);if(s)s.x=b.x+x;}
  if(!find(b,'Action / more'))R.iconButton(b,'more',660,24,null,theme);
  if(!find(b,'Action / minus'))R.iconButton(b,'minus',700,24,null,theme);
 }
 const scene=function(key,title,x){
  const b=R.board(key,title,x,-2060,1440,900);b.borderRadius=0;b.fills=[{fillColor:'#101B2D',fillOpacity:1}];
  R.text(b,'Scene label','WINDOWS  /  '+(key==='taskbarClosed'?'收起':'展开'),28,24,600,24,12,'#A1B2CB','500',R.font);
  R.rect(b,'Desktop accent',1100,92,264,560,'#15243C',26);
  R.rect(b,'Taskbar background',0,836,1440,64,'#192536');R.rect(b,'Taskbar top border',0,836,1440,1,'#2B394D');
  for(const [dx,dy] of [[0,0],[12,0],[0,12],[12,12]])R.rect(b,'Windows Start / tile',484+dx,857+dy,10,10,'#80B7FF',1);
  R.icon(b,'search',536,856,'#CFD9E8',24);R.icon(b,'folder',587,857,'#E6C57B',24);R.text(b,'Editor taskbar icon','<>',635,853,36,30,18,'#94A3B8','500',R.mono);
  const app=R.group(b,'Taskbar / RepoTracker button',688,844,48,48);R.rect(app,'Taskbar button background',0,0,48,48,key==='taskbarClosed'?'#233349':'#31445F',7);R.logo(app,9,7);R.rect(app,'Running indicator',key==='taskbarClosed'?20:13,44,key==='taskbarClosed'?8:22,3,key==='taskbarClosed'?'#AAB7C8':'#8AB4FF',2);
  R.text(b,'System tray symbols','⌃   EN',1221,855,95,30,13,'#B6C3D5');R.text(b,'System time','11:26',1344,847,80,24,13,'#E3EAF5','400',R.mono);R.text(b,'System date','2026/10/16',1322,872,104,18,10,'#B6C3D5','400',R.mono);
  return b;
 };
 if(!R.boards.taskbarClosed){const b=scene('taskbarClosed','26 / 任务栏入口 · 收起',0);R.text(b,'Collapsed title','RepoTracker，就在任务栏。',100,304,1160,65,42,'#EAF0FB','600');R.text(b,'Collapsed hint','点击底部的 RepoTracker 图标，展开仓库状态面板。',103,389,1080,36,20,'#A8B9D2');R.rect(b,'Button callout',592,749,240,52,'#223550',12);R.text(b,'Button callout label','点击展开仓库面板',621,763,200,28,16,'#CDE0FF','500');R.rect(b,'Callout stem',711,801,2,30,'#6D98D4');}
 if(!R.boards.taskbarOpen){const b=scene('taskbarOpen','27 / 任务栏入口 · 已展开',1540);const panel=R.boards.main.clone();panel.name='Expanded repository panel';b.appendChild(panel);panel.x=b.x+332;panel.y=b.y+226;R.taskbarPanel=panel;}
 for(const b of Object.values(R.boards)){
  for(const s of penpotUtils.findShapes(s=>s.interactions?.length>0,b)){
   for(const i of s.interactions){if(i.action.type==='navigate-to'&&i.action.destination.id===R.boards.main.id)i.action={type:'navigate-to',destination:R.boards.taskbarOpen};}
  }
 }
 nav(find(R.boards.taskbarClosed,'Taskbar / RepoTracker button'),'taskbarOpen');
 nav(find(R.boards.taskbarOpen,'Taskbar / RepoTracker button'),'taskbarClosed');
 nav(find(R.boards.taskbarClosed,'Button callout'),'taskbarOpen');
 for(const b of [...Object.values(R.boards),R.taskbarPanel]){
  const menu=find(b,'Action / more');if(menu)nav(menu,'tray');
  const collapse=find(b,'Action / minus');if(collapse)nav(collapse,'taskbarClosed');
 }
 nav(find(R.boards.guide,'Button / 开始浏览原型 →'),'taskbarClosed');
 for(const f of penpot.currentPage.flows){if(f.name==='RepoTracker / main')f.startingBoard=R.boards.taskbarClosed;if(f.name==='RepoTracker / tray')f.name='RepoTracker / menu';}
 for(const f of [...penpot.currentPage.flows]){if(/^Flow \d+$/.test(f.name)&&[R.boards.taskbarClosed.id,R.taskbarPanel.id].includes(f.startingBoard.id))penpot.currentPage.removeFlow(f);}
 penpotUtils.findShapes(s=>s.type==='text'&&s.characters.includes('托盘'),penpot.root).forEach(s=>{throw new Error('Unconverted tray copy: '+s.name);});
 R.taskbarRevisionComplete=true;
 penpot.viewport.zoomIntoView([R.boards.taskbarOpen]);
 return {boards:Object.keys(R.boards).length,collapsed:R.boards.taskbarClosed.id,expanded:R.boards.taskbarOpen.id};
}finally{penpot.history.undoBlockFinish(block);}
