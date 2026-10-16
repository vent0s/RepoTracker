// Public copy: replace the placeholder file/page IDs with your own target before running.
// Manual grouping design revision. Load, then call storage.rt.groupsBuild(key)
// for each key in groupsPlan, followed by groupsConnect(). No OS or Git writes.
const R=storage.rt;
if(penpot.currentFile.id!=='00000000-0000-4000-8000-000000000002'||penpot.currentPage.id!=='00000000-0000-4000-8000-000000000003')throw new Error('Wrong target');
if(!R.taskbarRevisionComplete)throw new Error('Taskbar revision required');
R.groupLinks=R.groupLinks||[];
R.groupsPlan=['grouped','groupCollapsed','groupCreate','groupCreated','groupMove','groupMoved','groupManage','groupRename','groupRenamed','groupDelete','groupDeleted','groupPinned'];
R.iconPaths.move='M4 8h14M14 4l4 4-4 4M20 16H6M10 12l-4 4 4 4';
R.iconPaths.right='M9 6l6 6-6 6';
R.gLink=(s,key)=>{R.groupLinks.push({s,key});return s;};
R.gButton=(b,label,x,y,w,to,kind='secondary')=>R.gLink(R.button(b,label,x,y,w,null,kind),to);
R.gFind=(b,name)=>penpotUtils.findShape(s=>s.name===name,b);
R.gNav=(s,key)=>{for(const i of [...s.interactions])s.removeInteraction(i);s.addInteraction('click',{type:'navigate-to',destination:R.boards[key]});};
R.gLayout=function(key,name,x,y,groups,{collapsed=false,notice=''}={}){
 const height=168+groups.reduce((n,g)=>n+44+(collapsed&&g.id==='work'?0:g.rows.length*64),0)+76;
 const b=R.board(key,name,x,y,760,height);R.header(b,'RepoTracker','6 个仓库 · 手动分组');
 for(const [kind,xx] of [['refresh',540],['pin',580],['settings',620]])R.gFind(b,'Action / '+kind).x=b.x+xx;
 R.iconButton(b,'more',660,24);R.iconButton(b,'minus',700,24);
 R.text(b,'Grouping mode',notice||'按分组查看',24,101,410,24,13,notice?R.dark.green:R.dark.muted,'500');
 R.gButton(b,'＋ 新建分组',480,94,124,'groupCreate');R.gButton(b,'管理分组',616,94,120,'groupManage');
 R.text(b,'Column label','跟踪的仓库',32,142,220,20,11,R.dark.dim);R.text(b,'Branch label','当前分支',292,142,130,20,11,R.dark.dim);R.text(b,'Status label','工作区 / 与上游比较',428,142,260,20,11,R.dark.dim);
 let yy=168;
 for(const g of groups){
  const closed=collapsed&&g.id==='work';const h=36+(closed?0:g.rows.length*64);
  const wrap=R.group(b,'Repo group / '+g.id,16,yy,728,h);
  const header=R.group(wrap,'Group toggle / '+g.id,0,0,728,32);R.rect(header,'Group header background',0,0,728,32,R.dark.surface,6);
  R.icon(header,closed?'right':'chevron',10,8,R.dark.muted,16);
  R.text(header,'Group name',g.name,36,5,380,24,13,R.dark.text,'500');
  R.text(header,'Group count',String(g.rows.length)+' 个仓库',630,6,87,22,11,R.dark.muted);
  if(g.id==='work'&&['grouped','groupCollapsed'].includes(key))R.gLink(header,closed?'grouped':'groupCollapsed');
  if(!closed)g.rows.forEach((item,i)=>{const row=R.row(wrap,item,i);row.x=wrap.x;row.y=wrap.y+36+i*64;
   if(item.name==='repotracker'){R.gFind(row,'Status timestamp').resize(188,19);const m=R.iconButton(row,'move',608,12);m.name='Move repository / repotracker';R.gLink(m,'groupMove');}
  });
  yy+=h+8;
 }
 R.rect(b,'Footer divider',24,yy,712,1,R.dark.line);R.gButton(b,'＋ 添加仓库',24,yy+16,130,'add','primary');R.text(b,'Local refresh time','本地检查 11:26:08',176,yy+21,240,23,11,R.dark.muted);R.text(b,'Readonly principle','只查看状态',635,yy+21,101,23,11,R.dark.muted);
 return b;
};
R.groupsBuild=function(key){
 if(!R.groupsPlan.includes(key))throw new Error('Unknown grouping board');if(R.boards[key])return {key,alreadyExists:true};
 const work={id:'work',name:'工作',rows:R.rows.slice(0,3)},personal={id:'personal',name:'个人',rows:R.rows.slice(3,5)},loose={id:'ungrouped',name:'未分组',rows:R.rows.slice(5)};
 let b;const undo=penpot.history.undoBlockBegin();try{
 if(key==='grouped')b=R.gLayout(key,'28 / 手动分组 · 仓库面板',0,-3380,[work,personal,loose]);
 if(key==='groupCollapsed')b=R.gLayout(key,'29 / 折叠工作分组',840,-3380,[work,personal,loose],{collapsed:true});
 if(key==='groupCreate'){
  b=R.dialog(key,'30 / 新建分组',1680,-3380,480,328,'新建分组','为一起工作的仓库起一个名字。');R.field(b,'分组名称','工具',24,118,432);R.text(b,'Group naming hint','1–30 个字符，同名分组不能重复。',24,204,432,24,11,R.dark.muted);R.gButton(b,'取消',224,264,100,'grouped');R.gButton(b,'创建分组',336,264,120,'groupCreated','primary');
 }
 if(key==='groupCreated')b=R.gLayout(key,'31 / 分组已创建 · 空组',2240,-3380,[work,personal,{id:'tools',name:'工具',rows:[]},loose],{notice:'已创建“工具”，可以把仓库移入分组。'});
 if(key==='groupMove'){
  b=R.dialog(key,'32 / 移动仓库到分组',3080,-3380,480,442,'移动到分组','repotracker · 当前位于“工作”');
  [['工作','当前分组',false],['个人','移入此组',true],['未分组','移出当前分组',false]].forEach(([label,hint,on],i)=>{const y=122+i*60;const row=R.group(b,'Group option / '+label,24,y,432,50);R.rect(row,'Choice background',0,0,432,50,on?R.dark.accentBg:R.dark.surface,7);R.text(row,'Choice name',label,16,12,200,28,14,on?R.dark.accent:R.dark.text,'500');R.text(row,'Choice hint',hint,286,15,130,24,11,R.dark.muted);if(on)R.gLink(row,'groupMoved');});
  R.text(b,'Move scope','只修改归类，磁盘目录和 Git 配置保持不变。',24,318,432,36,12,R.dark.muted);R.gButton(b,'取消',356,378,100,'grouped');
 }
 if(key==='groupMoved')b=R.gLayout(key,'33 / 仓库已移入个人',3640,-3380,[{...work,rows:R.rows.slice(1,3)},{...personal,rows:[R.rows[0],...personal.rows]},loose],{notice:'repotracker 已移入“个人”。'});
 if(key==='groupManage'){
  b=R.dialog(key,'34 / 管理手动分组',4480,-3380,560,426,'管理分组','重命名或删除分组，仓库跟踪记录始终保留。');
  [['工作','3 个仓库'],['个人','2 个仓库'],['未分组','1 个仓库 · 默认分组']].forEach(([name,count],i)=>{const y=120+i*68;R.rect(b,'Managed group / '+name,24,y,512,58,R.dark.surface,7);R.text(b,'Managed group name / '+name,name,40,y+10,140,24,14,R.dark.text,'500');R.text(b,'Managed group count',count,40,y+33,225,20,10,R.dark.muted);if(i===0)R.gButton(b,'重命名',416,y+11,108,'groupRename');if(i===1)R.gButton(b,'删除分组',416,y+11,108,'groupDelete');if(i===2)R.text(b,'System group note','不可重命名或删除',352,y+18,180,25,11,R.dark.dim);});
  R.gButton(b,'返回面板',396,362,140,'grouped');
 }
 if(key==='groupRename'){
  b=R.dialog(key,'35 / 重命名工作分组',5120,-3380,480,328,'重命名分组','修改显示名称，组内仓库保持原样。');R.field(b,'分组名称','工作项目',24,118,432);R.text(b,'Group naming hint','1–30 个字符，同名分组不能重复。',24,204,432,24,11,R.dark.muted);R.gButton(b,'取消',224,264,100,'groupManage');R.gButton(b,'保存名称',336,264,120,'groupRenamed','primary');
 }
 if(key==='groupRenamed')b=R.gLayout(key,'36 / 分组已重命名',0,-4460,[{...work,name:'工作项目'},personal,loose],{notice:'“工作”已重命名为“工作项目”。'});
 if(key==='groupDelete'){
  b=R.dialog(key,'37 / 删除个人分组',840,-4460,480,324,'删除“个人”分组？','组内 2 个仓库会移到“未分组”，继续跟踪。');R.rect(b,'Delete group assurance',24,127,432,72,R.dark.surface,8);R.text(b,'Delete scope','仅删除分组。\n不会移除仓库记录，也不会删除本地文件。',40,138,400,51,12,R.dark.muted);R.gButton(b,'保留分组',204,260,120,'groupManage');R.gButton(b,'删除分组',336,260,120,'groupDeleted','primary');
 }
 if(key==='groupDeleted')b=R.gLayout(key,'38 / 分组删除 · 仓库保留',1400,-4460,[work,{...loose,rows:[...personal.rows,...loose.rows]}],{notice:'“个人”已删除，2 个仓库移回未分组。'});
 if(key==='groupPinned'){b=R.boards.grouped.clone();b.name='39 / 分组面板 · 保持展开';b.x=2240;b.y=-4460;R.boards[key]=b;R.gFind(b,'Subtitle').characters='保持展开 · 6 个仓库 · 手动分组';R.gFind(R.gFind(b,'Action / pin'),'Hit area').fills=[{fillColor:R.dark.accentBg,fillOpacity:1}];}
 return {key,id:b.id,width:b.width,height:b.height};
 }finally{penpot.history.undoBlockFinish(undo);}
};
R.groupsConnect=function(){
 if(R.groupsRevisionComplete)return {alreadyApplied:true};
 for(const key of R.groupsPlan)if(!R.boards[key])throw new Error('Missing '+key);
 for(const {s,key} of R.groupLinks)R.gNav(s,key);
 for(const key of R.groupsPlan){const b=R.boards[key];
  for(const [name,to] of [['Button / ＋ 新建分组','groupCreate'],['Button / 管理分组','groupManage'],['Button / ＋ 添加仓库','add'],['Move repository / repotracker','groupMove']]){const s=R.gFind(b,name);if(s)R.gNav(s,to);}
  if(key==='groupPinned')R.gNav(R.gFind(b,'Group toggle / work'),'groupCollapsed');
  for(const s of penpotUtils.findShapes(s=>s.name.startsWith('Action / '),b)){
   const kind=s.name.slice(9),isRow=s.parent?.name.startsWith('Repository / ');
   if(isRow){if(kind==='folder')R.gNav(s,'folderNote');if(kind==='close'&&s.parent.name==='Repository / repotracker')R.gNav(s,'remove');}
   else if(kind==='close')R.gNav(s,'grouped');
   else if(kind==='settings')R.gNav(s,'settings');else if(kind==='more')R.gNav(s,'tray');else if(kind==='minus')R.gNav(s,'taskbarClosed');else if(kind==='refresh')R.gNav(s,'refreshNote');else if(kind==='pin')R.gNav(s,key==='groupPinned'?'grouped':'groupPinned');
  }
 }
 const scene=R.boards.taskbarOpen;const old=R.taskbarPanel;
 const panel=R.boards.grouped.clone();panel.name='Expanded repository panel';scene.appendChild(panel);panel.x=scene.x+332;panel.y=scene.y+816-panel.height;R.taskbarPanel=panel;old.remove();
 for(const f of [...penpot.currentPage.flows])if(/^Flow \d+$/.test(f.name)&&[panel.id,...R.groupsPlan.map(k=>R.boards[k].id)].includes(f.startingBoard.id))penpot.currentPage.removeFlow(f);
 if(!penpot.currentPage.flows.some(f=>f.name==='RepoTracker / groups'))penpot.currentPage.createFlow('RepoTracker / groups',R.boards.grouped);
 R.groupsRevisionComplete=true;penpot.viewport.zoomIntoView([scene]);
 return {boards:Object.keys(R.boards).length,panel:panel.id,flows:penpot.currentPage.flows.map(f=>f.name)};
};
return {keys:R.groupsPlan};
