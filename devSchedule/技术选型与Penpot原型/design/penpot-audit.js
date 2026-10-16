// Read-only geometry and interaction audit, executed through Penpot MCP.
const R=storage.rt;
const roots=Object.values(R.boards);
const ids=new Set(roots.map(b=>b.id));
const find=(b,name)=>penpotUtils.findShape(s=>s.name===name,b);
const out=[];const textOverflow=[];const broken=[];const overlaps=[];
let shapes=0,texts=0,interactions=0;
for(const b of roots){
 penpotUtils.analyzeDescendants(b,(root,s)=>{
  shapes++;if(s.type==='text')texts++;
  if(!penpotUtils.isContainedIn(s,root))out.push({board:b.name,name:s.name,bounds:s.bounds});
  if(s.type==='text'&&s.textBounds){const z=s.textBounds;if(z.x<s.x-2||z.y<s.y-2||z.x+z.width>s.x+s.width+2||z.y+z.height>s.y+s.height+2)textOverflow.push({board:b.name,name:s.name,text:s.characters,bounds:s.bounds,textBounds:z});}
  for(const i of s.interactions||[]){interactions++;if(i.action.type==='navigate-to'&&!ids.has(i.action.destination?.id))broken.push({board:b.name,name:s.name});}
  return null;
 });
}
for(let i=0;i<roots.length;i++)for(let j=i+1;j<roots.length;j++){const a=roots[i],b=roots[j];if(a.x<b.x+b.width&&a.x+a.width>b.x&&a.y<b.y+b.height&&a.y+a.height>b.y)overlaps.push([a.name,b.name]);}
const target=(b,name)=>find(b,name)?.interactions[0]?.action.destination?.id;
const routeChecks=[
 ['add-start',target(R.boards.main,'Button / ＋ 添加仓库')===R.boards.add.id],
 ['add-select',target(R.boards.add,'Button / 浏览…')===R.boards.ready.id],
 ['add-success',target(R.boards.ready,'Button / 添加仓库')===R.boards.added.id],
 ['first-add-start',target(R.boards.empty,'Button / ＋ 添加第一个仓库')===R.boards.firstReady.id],
 ['first-add-success',target(R.boards.firstReady,'Button / 添加仓库')===R.boards.firstMain.id],
 ['remove-success',target(R.boards.remove,'Button / 移除跟踪')===R.boards.removed.id],
 ['remove-undo',target(R.boards.removed,'Button / 撤销')===R.boards.taskbarOpen.id],
 ['source-save',target(R.boards.sourceAdd,'Button / 保存规则')===R.boards.sourceSaved.id],
 ['source-result',target(R.boards.sourceSaved,'Button / 返回已知来源')===R.boards.sourcesAdded.id],
 ['remote-disabled',target(R.boards.settingsOff,'Button / 返回面板')===R.boards.remoteOff.id],
 ['guide-taskbar',target(R.boards.guide,'Button / 开始浏览原型 →')===R.boards.taskbarClosed.id],
 ['taskbar-expand',target(R.boards.taskbarClosed,'Taskbar / RepoTracker button')===R.boards.taskbarOpen.id],
 ['taskbar-collapse',target(R.boards.taskbarOpen,'Taskbar / RepoTracker button')===R.boards.taskbarClosed.id],
 ['panel-collapse',target(R.taskbarPanel,'Action / minus')===R.boards.taskbarClosed.id],
 ['panel-settings',target(R.taskbarPanel,'Action / settings')===R.boards.settings.id],
 ['panel-menu',target(R.taskbarPanel,'Action / more')===R.boards.tray.id],
 ['group-fold',target(R.boards.grouped,'Group toggle / work')===R.boards.groupCollapsed.id],
 ['group-unfold',target(R.boards.groupCollapsed,'Group toggle / work')===R.boards.grouped.id],
 ['group-create-start',target(R.taskbarPanel,'Button / ＋ 新建分组')===R.boards.groupCreate.id],
 ['group-create-save',target(R.boards.groupCreate,'Button / 创建分组')===R.boards.groupCreated.id],
 ['group-move-start',target(R.taskbarPanel,'Move repository / repotracker')===R.boards.groupMove.id],
 ['group-move-result',target(R.boards.groupMove,'Group option / 个人')===R.boards.groupMoved.id],
 ['group-manage',target(R.taskbarPanel,'Button / 管理分组')===R.boards.groupManage.id],
 ['group-rename-start',target(R.boards.groupManage,'Button / 重命名')===R.boards.groupRename.id],
 ['group-rename-save',target(R.boards.groupRename,'Button / 保存名称')===R.boards.groupRenamed.id],
 ['group-delete-start',target(R.boards.groupManage,'Button / 删除分组')===R.boards.groupDelete.id],
 ['group-delete-result',target(R.boards.groupDelete,'Button / 删除分组')===R.boards.groupDeleted.id],
 ['group-pin',target(R.taskbarPanel,'Action / pin')===R.boards.groupPinned.id],
 ['group-unpin',target(R.boards.groupPinned,'Action / pin')===R.boards.grouped.id]
];
const rowCounts=Object.fromEntries(['main','added','removed','firstMain'].map(k=>[k,R.boards[k].children.filter(s=>s.name.startsWith('Repository / ')).length]));
const stateChecks={rowCounts,rowCountsValid:rowCounts.main===6&&rowCounts.added===7&&rowCounts.removed===5&&rowCounts.firstMain===1,newSourceVisible:!!find(R.boards.sourcesAdded,'New host pattern'),mainNoUpstream:!!penpotUtils.findShape(s=>s.type==='text'&&s.characters==='无上游',R.boards.main),remoteOffLabel:find(R.boards.remoteOff,'Subtitle').characters.includes('已关闭'),taskbarFlowStart:penpot.currentPage.flows.find(f=>f.name==='RepoTracker / main')?.startingBoard.id===R.boards.taskbarClosed.id,noTrayEntryCopy:!penpotUtils.findShape(s=>s.type==='text'&&s.characters.includes('托盘'),penpot.root)};
const memberships=Object.fromEntries(['grouped','groupCreated','groupCollapsed','groupMoved','groupRenamed','groupDeleted'].map(k=>[k,Object.fromEntries(R.boards[k].children.filter(s=>s.name.startsWith('Repo group / ')).map(g=>[g.name.slice(13),g.children.filter(s=>s.name.startsWith('Repository / ')).map(s=>s.name.slice(13))]))]));
const allMembers=k=>Object.values(memberships[k]).flat();const sorted=k=>allMembers(k).sort().join('|');
const groupingChecks={memberships,initialCounts:JSON.stringify(Object.values(memberships.grouped).map(a=>a.length))==='[3,2,1]',createdEmpty:memberships.groupCreated.tools.length===0,foldOnlyHides:allMembers('groupCollapsed').length===3&&find(R.boards.groupCollapsed,'Group count').characters==='3 个仓库',moveUnique:allMembers('groupMoved').length===6&&new Set(allMembers('groupMoved')).size===6&&memberships.groupMoved.personal.includes('repotracker')&&!memberships.groupMoved.work.includes('repotracker'),renamePreservesMembership:sorted('groupRenamed')===sorted('grouped')&&find(R.boards.groupRenamed,'Group name').characters==='工作项目',deletePreservesAll:sorted('groupDeleted')===sorted('grouped')&&!memberships.groupDeleted.personal&&memberships.groupDeleted.ungrouped.length===3,taskbarGrouped:!!find(R.taskbarPanel,'Repo group / work')};
return {file:{id:penpot.currentFile.id,name:penpot.currentFile.name},page:{id:penpot.currentPage.id,name:penpot.currentPage.name},boardCount:roots.length,shapes,texts,interactions,flows:penpot.currentPage.flows.map(f=>({name:f.name,startingBoardId:f.startingBoard.id})),components:penpot.library.local.components.filter(c=>(c.path||'').includes('RepoTracker')).length,colors:penpot.library.local.colors.filter(c=>(c.path||'').includes('RepoTracker')).length,boards:Object.fromEntries(Object.entries(R.boards).map(([k,b])=>[k,{id:b.id,name:b.name,x:b.x,y:b.y,width:b.width,height:b.height}])),outsideBoards:out,textOverflow,brokenTargets:broken,overlappingBoards:overlaps,routeChecks,stateChecks,groupingChecks};
