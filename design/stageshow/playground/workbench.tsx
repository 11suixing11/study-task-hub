import { StrictMode, useState } from 'react'
import { createRoot } from 'react-dom/client'
import LearningWorkbench from '../examples/learning-workbench'
import './workbench.css'
function Preview() {
  const [compact, setCompact] = useState(false)
  const [version, setVersion] = useState(0)
  return <div className="wb-host"><header className="wb-toolbar"><div className="wb-label"><strong>STAGESHOW</strong><span>聚合平台 / Windows 界面预览</span></div><div className="wb-controls"><div className="wb-sizes"><button aria-pressed={!compact} onClick={() => setCompact(false)}>桌面</button><button aria-pressed={compact} onClick={() => setCompact(true)}>窄屏</button></div><button onClick={() => setVersion(v => v + 1)}>重置演示</button><a href="/?example=learning-workbench" target="_blank" rel="noreferrer">Stageshow 查看器 ↗</a></div></header><div className="wb-description"><span className="wb-live" />可交互预览<span className="wb-description-separator">·</span>演示数据，可试用筛选、任务详情与课程管理</div><div className={`wb-canvas${compact ? ' is-compact' : ''}`}><LearningWorkbench key={version} /></div><footer className="wb-footer"><span>桌面三栏工作台 · 灰白表面与蓝色主操作</span><span>React + Stageshow</span></footer></div>
}
export function mount() { document.title = '聚合平台 · Stageshow 界面预览'; document.documentElement.lang = 'zh-CN'; createRoot(document.getElementById('root')!).render(<StrictMode><Preview /></StrictMode>) }
