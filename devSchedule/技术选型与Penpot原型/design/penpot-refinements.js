// Public copy: replace the placeholder file/page IDs with your own target before running.
// Run after all five build phases, before connect(). Safe to re-run.
const R=storage.rt;
if(penpot.currentFile.id!=='00000000-0000-4000-8000-000000000002')throw new Error('Wrong file');
const find=(b,name)=>penpotUtils.findShape(s=>s.name===name,b);
const retarget=(shape,to)=>{const link=R.links.find(l=>l.shape.id===shape.id);if(link)link.to=to;else R.link(shape,to);};
if(!R.boards.firstReady){
 const b=R.boards.ready.clone();b.name='23 / 首次添加 · 校验通过';b.x=1680;b.y=2180;R.boards.firstReady=b;
 R.link(find(b,'Button / 添加仓库'),'firstMain');R.link(find(b,'Button / 重新选择'),'empty');R.link(find(b,'Action / close'),'empty');
}
if(!R.boards.firstMain){
 const b=R.panel('firstMain','24 / 首次添加 · 1 个仓库',2320,2180,R.dark,{items:[{name:'build-tools',path:'E:\\Projects\\build-tools',branch:'main',provider:'github',badges:[['✓ 干净',64,'green'],['↑ 0',42,'muted'],['↓ 0',42,'muted']],note:'仅本地数据 · 尚未远程检查'}]});
 b.resize(760,350);find(b,'Footer divider').y=b.y+278;find(b,'Button / ＋ 添加仓库').y=b.y+295;find(b,'Local refresh time').y=b.y+297;find(b,'Readonly principle').y=b.y+297;
 R.text(b,'First add success','已开始跟踪 build-tools。',32,219,680,27,13,R.dark.green);
}
retarget(find(R.boards.empty,'Button / ＋ 添加第一个仓库'),'firstReady');
if(!R.boards.remoteOff){
 const b=R.boards.main.clone();b.name='25 / 远程检查关闭 · 本地模式';b.x=3160;b.y=2180;R.boards.remoteOff=b;
 find(b,'Subtitle').characters='6 个仓库 · 远程检查已关闭';
 for(const row of b.children.filter(s=>s.name.startsWith('Repository / '))){const n=find(row,'Status timestamp');if(n.characters.startsWith('远程数据'))n.characters='远程检查关闭 · 截至 11:24';const folder=find(row,'Action / folder');if(folder)R.link(folder,'folderNote');if(row.name==='Repository / repotracker')R.link(find(row,'Action / close'),'remove');}
 R.link(find(b,'Action / settings'),'settingsOff');R.link(find(b,'Action / refresh'),'refreshNote');R.link(find(b,'Action / pin'),'pinned');R.link(find(b,'Button / ＋ 添加仓库'),'add');
}
retarget(find(R.boards.settingsOff,'Button / 返回面板'),'remoteOff');
find(R.boards.settingsOff,'Description / 远程检查').characters='已关闭。领先 / 落后仅基于本地记录，不访问远程。';
find(R.boards.settingsStartup,'Description / 开机启动').characters='已开启。登录 Windows 后在托盘运行。';
R.links=R.links.filter(({shape})=>!(shape.name==='Action / close'&&shape.parent?.name.startsWith('Repository / ')&&shape.parent.name!=='Repository / repotracker'));
for(const c of penpot.library.local.components){if((c.path||'').includes('RepoTracker'))c.path='RepoTracker / Status';}
for(const [name,value] of Object.entries(R.light)){if(!penpot.library.local.colors.some(c=>c.name===name&&c.path==='RepoTracker / Light')){const c=penpot.library.local.createColor();c.name=name;c.path='RepoTracker / Light';c.color=value;}}
return {boards:Object.keys(R.boards).length,links:R.links.length};
