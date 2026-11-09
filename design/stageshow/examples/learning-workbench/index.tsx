import { useEffect, useMemo, useRef, useState, type KeyboardEvent, type ComponentProps } from 'react'
import { Badge, Button, Card, Field, Icon, Row, Stage, Text, Toast, Window } from '../../kit'
import './workbench.css'

type Task = { id: number; title: string; course: string; platform: string; deadline: string; date: string; kind: string; done?: boolean; due?: 'soon' | 'overdue'; note: string }
type Filter = 'active' | 'all' | 'soon' | 'overdue' | 'done'
const PLATFORMS = ['学习通', '雨课堂', '长江雨课堂', '中国大学 MOOC', '学堂在线', '智慧树', 'U 校园', 'PTA']
const INITIAL: Task[] = [
  { id: 1, title: '栈与队列 · 题目集', course: '数据结构', platform: 'PTA', deadline: '今天 23:59', date: '2026-11-05 23:59', kind: '编程作业', due: 'soon', note: '先梳理顺序栈与链式队列，再回到课程完成练习。' },
  { id: 2, title: 'Unit 4 · 阅读与词汇', course: '大学英语', platform: 'U 校园', deadline: '今天 20:00', date: '2026-11-05 20:00', kind: '单元练习', due: 'soon', note: '复习本单元的重点词汇与阅读材料。' },
  { id: 3, title: '第一章 · 线性表综合练习', course: '数据结构', platform: 'PTA', deadline: '昨天 22:00', date: '2026-11-04 22:00', kind: '编程作业', due: 'overdue', note: '截止时间已过，请到官方页面核对是否允许补交。' },
  { id: 4, title: '极限与连续 · 课后练习', course: '高等数学', platform: '学习通', deadline: '明天 20:00', date: '2026-11-06 20:00', kind: '课后作业', note: '核对第二节的学习笔记。' },
  { id: 5, title: '第六讲 · 随堂测验', course: '计算机网络', platform: '雨课堂', deadline: '11 月 07 日', date: '2026-11-07 18:00', kind: '随堂测验', note: '完成本讲视频后复习分层结构。' },
  { id: 6, title: '模型与推理 · 课程测验', course: '人工智能导论', platform: '智慧树', deadline: '11 月 08 日', date: '2026-11-08 23:59', kind: '课程测验', note: '重新登录后可更新课程最新进度。' },
  { id: 7, title: '矩阵运算 · 单元测试', course: '线性代数', platform: '中国大学 MOOC', deadline: '11 月 09 日', date: '2026-11-09 23:59', kind: '单元测试', note: '整理矩阵初等变换相关公式。' },
  { id: 8, title: '模块三 · 课后复习', course: '程序设计基础', platform: '学堂在线', deadline: '11 月 10 日', date: '2026-11-10 18:00', kind: '课后作业', note: '本周的课后复习安排。' },
  { id: 9, title: '循环与分支 · 练习二', course: '程序设计基础', platform: 'PTA', deadline: '11 月 03 日', date: '2026-11-03 23:59', kind: '编程作业', done: true, note: '已完成，等待课程更新。' },
  { id: 10, title: '导数与微分 · 随堂练习', course: '高等数学', platform: '学习通', deadline: '11 月 02 日', date: '2026-11-02 20:00', kind: '课后作业', done: true, note: '已完成本次练习。' },
  { id: 11, title: 'Unit 3 · 听力训练', course: '大学英语', platform: 'U 校园', deadline: '11 月 01 日', date: '2026-11-01 20:00', kind: '听力练习', done: true, note: '本单元学习记录。' },
  { id: 12, title: '导论 · 课堂测试', course: '大学物理', platform: '长江雨课堂', deadline: '10 月 31 日', date: '2026-10-31 18:00', kind: '随堂测验', done: true, note: '已完成课堂测试。' },
]
const FILTERS: { id: Filter; title: string }[] = [{ id: 'active', title: '待办任务' }, { id: 'all', title: '全部任务' }, { id: 'done', title: '已完成' }]
const priority = (task: Task) => task.done ? 4 : task.due === 'overdue' ? 0 : task.due === 'soon' ? 1 : task.date === '9999' ? 3 : 2
const keyboard = (run: () => void) => (e: KeyboardEvent<HTMLElement>) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); run() } }
function Action({ children, onClick, ...rest }: ComponentProps<typeof Button>) {
  return <Button {...rest} tabIndex={rest['aria-disabled'] ? -1 : 0} onClick={e => { if (!rest['aria-disabled']) onClick?.(e) }} onKeyDown={e => { if (!rest['aria-disabled'] && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); e.currentTarget.click() } }}>{children}</Button>
}
function Status({ task }: { task: Task }) {
  return <Badge tone={task.done ? 'success' : task.due === 'overdue' ? 'danger' : task.due === 'soon' ? 'warning' : 'neutral'}>{task.done ? '已完成' : task.due === 'overdue' ? '已逾期' : task.due === 'soon' ? '即将到期' : '待完成'}</Badge>
}
export default function LearningWorkbench() {
  return <Stage width={1200} height={780} compact={{ below: 850, width: 358, height: 780 }} className="lw-workbench" fidelity="realistic" greys="cool" accent="#3863eb"
    tokens={{ '--ss-font': '"Microsoft YaHei UI", "Segoe UI", system-ui, sans-serif', '--ss-canvas': '#f7f8fa', '--ss-surface': '#ffffff', '--ss-text-strong': '#202735', '--ss-text': '#687386', '--ss-text-faint': '#758092', '--ss-radius-window': '0px', '--ss-shadow-window': 'none', '--ss-control': '36px' }}>
    {(_, __, layout) => <Workbench compact={layout === 'compact'} />}
  </Stage>
}
function Workbench({ compact }: { compact: boolean }) {
  const [tasks, setTasks] = useState(INITIAL)
  const [filter, setFilter] = useState<Filter>('active')
  const [platform, setPlatform] = useState('全部平台')
  const [query, setQuery] = useState('')
  const [sort, setSort] = useState('priority')
  const [selection, setSelection] = useState(1)
  const [mobileDetail, setMobileDetail] = useState(false)
  const [dialog, setDialog] = useState<'courses' | 'add' | null>(null)
  const [coursePlatform, setCoursePlatform] = useState('PTA')
  const [courseDraft, setCourseDraft] = useState<string[]>(['PTA / 数据结构'])
  const [courses, setCourses] = useState<string[]>(['PTA / 数据结构'])
  const [title, setTitle] = useState('')
  const [course, setCourse] = useState('')
  const [syncing, setSyncing] = useState(false)
  const [lastSync, setLastSync] = useState('10:42')
  const [notice, setNotice] = useState('')
  const timer = useRef<ReturnType<typeof setTimeout>>()
  const toastTimer = useRef<ReturnType<typeof setTimeout>>()
  const dialogRef = useRef<HTMLDivElement>(null)
  const listRef = useRef<HTMLDivElement>(null)
  useEffect(() => () => { clearTimeout(timer.current); clearTimeout(toastTimer.current) }, [])
  useEffect(() => { if (!dialog) return; const previous = document.activeElement as HTMLElement | null; const id = requestAnimationFrame(() => dialogRef.current?.querySelector<HTMLElement>('[tabindex="0"], input, select')?.focus()); return () => { cancelAnimationFrame(id); previous?.focus() } }, [dialog])
  const notify = (text: string) => { clearTimeout(toastTimer.current); setNotice(text); toastTimer.current = setTimeout(() => setNotice(''), 3800) }
  const counts = { active: tasks.filter(t => !t.done).length, soon: tasks.filter(t => !t.done && t.due === 'soon').length, overdue: tasks.filter(t => !t.done && t.due === 'overdue').length, done: tasks.filter(t => t.done).length }
  const visible = useMemo(() => tasks.filter(t => (platform === '全部平台' || t.platform === platform) && (filter === 'all' || (filter === 'done' ? t.done : !t.done && (filter === 'active' || t.due === filter))) && `${t.title} ${t.course} ${t.platform} ${t.note}`.toLocaleLowerCase().includes(query.trim().toLocaleLowerCase())).sort((a, b) => (sort === 'priority' ? priority(a) - priority(b) : 0) || a.date.localeCompare(b.date) || a.id - b.id), [tasks, filter, platform, query, sort])
  const selected = visible.find(t => t.id === selection) ?? visible[0]
  useEffect(() => {
    const list = listRef.current
    const row = list?.querySelector<HTMLElement>(`[data-testid="task-${selected?.id}"]`)
    if (!list || !row) return
    const top = row.offsetTop - list.offsetTop
    if (top < list.scrollTop) list.scrollTop = top
    else if (top + row.offsetHeight > list.scrollTop + list.clientHeight) list.scrollTop = top + row.offsetHeight - list.clientHeight
  }, [selected?.id, filter, platform, query, sort, mobileDetail])
  const chooseFilter = (next: Filter) => { setFilter(next); setMobileDetail(false) }
  const choosePlatform = (next: string) => { setPlatform(next); setMobileDetail(false) }
  const openCourses = () => { setCoursePlatform(platform === '全部平台' ? 'PTA' : platform); setCourseDraft(courses); setDialog('courses') }
  const sync = () => { if (syncing) return; setSyncing(true); timer.current = setTimeout(() => { setSyncing(false); setLastSync(new Date().toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' })); notify('同步演示完成 · 任务清单已更新') }, 1500) }
  const complete = () => { if (!selected) return; setTasks(prev => prev.map(t => t.id === selected.id ? { ...t, done: !t.done } : t)); notify(selected.done ? '已恢复为待办任务' : '已标记完成 · 可在「已完成」中恢复'); setMobileDetail(false) }
  const courseOptions = [...new Set(INITIAL.filter(t => t.platform === coursePlatform).map(t => t.course))]
  if (!courseOptions.length) courseOptions.push('2026 秋季课程')
  const trap = (e: KeyboardEvent<HTMLDivElement>) => {
    if (e.key === 'Escape') { setDialog(null); e.stopPropagation() }
    if (e.key !== 'Tab') return
    const controls = [...e.currentTarget.querySelectorAll<HTMLElement>('[tabindex="0"], input, select')].filter(el => !el.hasAttribute('disabled')), first = controls[0], last = controls.at(-1)
    if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last?.focus() } else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first?.focus() }
  }
  return <Window chrome="content" className="lw-window" body={{ className: 'lw-app' }} aria-label="聚合平台学习工作台">
    <header className="lw-topbar"><div className="lw-wordmark"><Text className="lw-brand">聚合平台</Text><span className="lw-divider" /><Text className="lw-muted">学习工作台</Text></div><div className="lw-top-actions"><Text className="lw-last-sync">{`上次同步 ${lastSync}`}</Text><Action size="large" icon={<Icon name="arrow" size={16} />} onClick={sync} aria-disabled={syncing} data-testid="sync">{syncing ? '正在同步…' : '同步任务'}</Action></div></header>
    <div className="lw-body">
      {!compact && <aside className="lw-sidebar"><div className="lw-nav-group"><Text className="lw-eyebrow">工作空间</Text><Row pad={12} role="button" tabIndex={0} selected={platform === '全部平台'} onClick={() => choosePlatform('全部平台')} onKeyDown={keyboard(() => choosePlatform('全部平台'))} lead={<Icon name="home" size={16} />} title="所有学习任务" meta={<Text className="lw-nav-count">{String(tasks.length)}</Text>} /></div>
        <div className="lw-nav-group lw-platforms"><div className="lw-section-heading"><Text className="lw-eyebrow">课程平台</Text><Text className="lw-muted">8</Text></div>{PLATFORMS.map(name => <Row key={name} pad={12} selected={platform === name} role="button" tabIndex={0} aria-pressed={platform === name} onClick={() => choosePlatform(name)} onKeyDown={keyboard(() => choosePlatform(name))} title={<Text className="lw-nav-name">{name}</Text>} meta={<Text className="lw-nav-count">{String(tasks.filter(t => t.platform === name && !t.done).length || '—')}</Text>} />)}</div>
        <Card pad="small" gap={10} tone="sunken" className="lw-connection-note"><div className="lw-inline"><Icon name="lock" size={16} /><Text className="lw-small-strong">课程与登录</Text></div><Text className="lw-muted">管理课程范围与平台连接。</Text><Action onClick={openCourses}>管理已选课程</Action></Card><div className="lw-sidebar-foot"><Icon name="archive" size={16} /><Text className="lw-muted">本地保存 · 随时继续</Text></div></aside>}
      <main className="lw-main" aria-label="任务清单" data-mobile-hidden={compact && mobileDetail || undefined}>
        <div className="lw-page-heading"><div><Text className="lw-date">11 月 5 日，星期四</Text><Text className="lw-title">{platform === '全部平台' ? '统一任务清单' : platform}</Text></div><Action size="large" variant="primary" icon={<Icon name="plus" size={16} />} onClick={() => { setTitle(''); setCourse(''); setDialog('add') }} data-testid="add">新增任务</Action></div>
        <div className="lw-metrics">{([{ id: 'active', label: '待办任务', count: counts.active }, { id: 'soon', label: '即将到期', count: counts.soon }, { id: 'overdue', label: '已逾期', count: counts.overdue }, { id: 'done', label: '已完成', count: counts.done }] as const).map(metric => <Card key={metric.id} pad="small" gap={8} role="button" tabIndex={0} onClick={() => chooseFilter(metric.id)} onKeyDown={keyboard(() => chooseFilter(metric.id))} aria-pressed={filter === metric.id} className={`lw-metric lw-metric-${metric.id}`}><Text className="lw-metric-label">{metric.label}</Text><div className="lw-metric-value"><Text className="lw-stat-digit">{String(metric.count)}</Text><Icon name={metric.id === 'done' ? 'check' : metric.id === 'active' ? 'file' : 'clock'} size={16} /></div></Card>)}</div>
        <div className="lw-search"><Field value={query} placeholder="搜索任务、课程或备注" leading={<Icon name="search" size={16} />} input={{ 'aria-label': '搜索任务', onChange: e => setQuery(e.target.value) }} />{compact && <div className="lw-mobile-platform"><select className="lw-select lw-platform-select" aria-label="筛选平台" value={platform} onChange={e => choosePlatform(e.target.value)}><option>全部平台</option>{PLATFORMS.map(p => <option key={p}>{p}</option>)}</select><Action onClick={openCourses}>管理课程</Action></div>}</div>
        <div className="lw-list-toolbar"><div className="lw-tabs" role="tablist" aria-label="任务状态">{FILTERS.map(item => <Action key={item.id} size="medium" variant={filter === item.id ? 'tertiary' : 'ghost'} role="tab" aria-selected={filter === item.id} onClick={() => chooseFilter(item.id)}>{item.title}</Action>)}</div><select className="lw-select lw-sort" aria-label="任务排序" value={sort} onChange={e => setSort(e.target.value)}><option value="priority">优先级排序</option><option value="deadline">截止时间排序</option></select></div>
        <div className="lw-list-caption"><Text className="lw-muted">{filter === 'soon' ? '24 小时内到期' : filter === 'overdue' ? '需要留意的逾期任务' : '任务 / 课程'}</Text><Text className="lw-muted">截止时间</Text></div>
        <div ref={listRef} className="lw-task-scroll" data-testid="task-list">{visible.map(task => <Row key={task.id} pad={12} className="lw-task-row" role="button" tabIndex={0} selected={selected?.id === task.id} aria-pressed={selected?.id === task.id} data-testid={`task-${task.id}`} onClick={() => { setSelection(task.id); setMobileDetail(true) }} onKeyDown={keyboard(() => { setSelection(task.id); setMobileDetail(true) })} lead={<span className={`lw-task-check${task.done ? ' is-done' : ''}`} aria-hidden="true">{task.done && <Icon name="check" size={14} />}</span>} title={<Text className="lw-task-title">{task.title}</Text>} subtitle={<Text className="lw-task-subtitle">{`${task.platform} / ${task.course}`}</Text>} meta={<Text className={`lw-due${task.due && !task.done ? ` is-${task.due}` : ''}`}>{task.deadline}</Text>} />)}
          {!visible.length && <div className="lw-empty"><Icon name="search" size={24} /><Text className="lw-section-title">{query ? '没有找到相关任务' : '这里暂时没有任务'}</Text><Text className="lw-muted">调整筛选条件，或添加一项学习任务。</Text><Action onClick={() => { setFilter('all'); setPlatform('全部平台'); setQuery('') }}>查看全部任务</Action></div>}</div>
        <footer className="lw-list-footer"><Text className="lw-muted">{`显示 ${visible.length} 项 · 共 ${tasks.length} 项`}</Text><Action variant="ghost" size="small" onClick={() => { setFilter('all'); setPlatform('全部平台'); setQuery('') }}>查看全部</Action></footer>
      </main>
      {(!compact || mobileDetail) && <aside className="lw-detail" aria-label="任务详情"><div className="lw-detail-top"><Text className="lw-eyebrow">任务详情</Text>{compact ? <Action variant="ghost" size="small" onClick={() => setMobileDetail(false)}>返回清单</Action> : <Icon name="file" size={16} />}</div>{selected ? <>
        <div className="lw-detail-intro"><div className="lw-inline"><Badge tone="accent">{selected.platform}</Badge><Text className="lw-muted">{selected.kind}</Text></div><Text className="lw-detail-title">{selected.title}</Text><Text className="lw-detail-course">{selected.course}</Text></div>
        <Card pad="medium" gap={12} className="lw-deadline-card" tone="sunken"><div className="lw-inline"><Icon name="calendar" size={16} /><Text className="lw-muted">截止时间</Text></div><Text className={`lw-deadline-value${selected.due && !selected.done ? ` is-${selected.due}` : ''}`}>{selected.deadline}</Text><Text className="lw-muted">{selected.date === '9999' ? '未设置截止日期' : selected.date}</Text></Card>
        <div className="lw-detail-facts"><div><Text className="lw-muted">我的状态</Text><Status task={selected} /></div><div><Text className="lw-muted">平台记录</Text><Text className="lw-small-strong">{selected.platform === '手动添加' ? '手动记录' : INITIAL.find(t => t.id === selected.id)?.done ? '已提交' : '未完成'}</Text></div><div><Text className="lw-muted">最近同步</Text><Text className="lw-small-strong">{`今天 ${lastSync}`}</Text></div></div>
        <div className="lw-notes"><Text className="lw-eyebrow">学习备注</Text><Text className="lw-note-text">{selected.note}</Text></div><div className="lw-detail-actions"><Action size="large" variant="primary" trailingIcon={<Icon name="external" size={16} />} onClick={() => notify('演示任务没有真实链接，请在 Windows 程序中打开官方任务。')} data-testid="official">打开官方任务</Action><Action size="large" icon={<Icon name={selected.done ? 'arrow' : 'check'} size={16} />} onClick={complete} data-testid="complete">{selected.done ? '恢复为待办' : '标记完成'}</Action><Text className="lw-action-hint">手动标记只更新你的本地任务清单</Text></div></> : <div className="lw-empty"><Icon name="file" size={24} /><Text className="lw-section-title">选中任务，查看详情</Text><Text className="lw-muted">任务的状态、时间和备注会显示在这里。</Text></div>}</aside>}
    </div>
    <div className="lw-statusbar"><div className="lw-inline"><span className="lw-status-dot" /><Text className="lw-status-text">{syncing ? '正在更新任务记录…' : '任务已保存到本地'}</Text></div><Text className="lw-status-text">聚合平台 · Windows</Text></div><div className="lw-toast-wrap"><Toast show={!!notice} icon={<Icon name="check" size={16} />}>{notice}</Toast></div>
    {dialog && <div className="lw-overlay" onMouseDown={e => { if (e.target === e.currentTarget) setDialog(null) }}><Card pad="large" gap={20} className="lw-dialog" role="dialog" aria-modal="true" aria-labelledby="lw-dialog-title"><div ref={dialogRef} className="lw-dialog-content" onKeyDown={trap}>
      <div className="lw-dialog-header"><div><Text className="lw-section-title" id="lw-dialog-title">{dialog === 'courses' ? '管理课程' : '新增学习任务'}</Text><Text className="lw-muted">{dialog === 'courses' ? '只记录你关心的课程' : '把自己的学习安排也放到这里'}</Text></div><Action variant="ghost" size="small" aria-label="关闭面板" onClick={() => setDialog(null)} icon={<Icon name="close" size={16} />} /></div>
      {dialog === 'courses' ? <><label className="lw-form-label">课程平台<select className="lw-select lw-form-select" aria-label="管理课程的平台" value={coursePlatform} onChange={e => setCoursePlatform(e.target.value)}>{PLATFORMS.map(p => <option key={p}>{p}</option>)}</select></label><div className="lw-courses">{courseOptions.map(name => { const id = `${coursePlatform} / ${name}`, checked = courseDraft.includes(id), toggle = () => setCourseDraft(prev => checked ? prev.filter(c => c !== id) : [...prev, id]); return <Row key={id} pad={12} selected={checked} role="checkbox" tabIndex={0} aria-checked={checked} onClick={toggle} onKeyDown={keyboard(toggle)} title={name} subtitle="2026 秋季学期" lead={<span className={`lw-task-check${checked ? ' is-done' : ''}`}>{checked && <Icon name="check" size={14} />}</span>} /> })}</div><Text className="lw-muted">{`共选择 ${courseDraft.length} 门课程`}</Text><div className="lw-dialog-buttons"><Action onClick={() => setDialog(null)}>取消</Action><Action variant="primary" onClick={() => { setCourses(courseDraft); setDialog(null); notify('已保存演示课程选择') }}>保存选择</Action></div></> : <>
      <Field label="任务名称" value={title} placeholder="例如：整理第六章笔记" input={{ 'aria-label': '任务名称', maxLength: 80, onChange: e => setTitle(e.target.value) }} /><Field label="所属课程" value={course} placeholder="例如：数据结构" input={{ 'aria-label': '所属课程', maxLength: 40, onChange: e => setCourse(e.target.value) }} /><Text className="lw-muted">新任务将归入「手动添加」，暂不设置截止时间。</Text><div className="lw-dialog-buttons"><Action onClick={() => setDialog(null)}>取消</Action><Action variant="primary" aria-disabled={!title.trim()} onClick={() => { const id = Math.max(...tasks.map(t => t.id)) + 1; setTasks(prev => [...prev, { id, title: title.trim(), course: course.trim() || '个人学习', platform: '手动添加', kind: '学习安排', deadline: '未设置', date: '9999', note: '手动添加的学习安排，可随时标记完成。' }]); setFilter('active'); setPlatform('全部平台'); setQuery(''); setSelection(id); setDialog(null); notify('已添加学习任务') }}>添加任务</Action></div></>}
    </div></Card></div>}
  </Window>
}
