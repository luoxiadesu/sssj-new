"""Local card/weapon pool scheduler. Python standard library only."""
import argparse
from collections import defaultdict
from datetime import datetime, timedelta, timezone
import json
import os
from pathlib import Path
import shutil
import tempfile

ROOT = next((parent for parent in Path(__file__).resolve().parents
             if (parent / 'extracted/setting/gacha.txt').is_file()), Path(__file__).resolve().parents[1])
SETTINGS = ROOT / 'extracted' / 'setting'
PLAN = SETTINGS / 'gacha-rotation.json'
LANGUAGE = ROOT / 'extracted/lang-zh-CN.json'
ZONE = timezone(timedelta(hours=8))
FORMAT = '%Y-%m-%d %H:%M'


def catalog():
    lines = (SETTINGS / 'gacha.txt').read_text(encoding='utf-8-sig').splitlines()
    headers = lines[2].split('\t')
    lang = json.loads(LANGUAGE.read_text(encoding='utf-8')) if LANGUAGE.is_file() else {}
    result, sequence = [], defaultdict(int)
    for line in lines[3:]:
        row = dict(zip(headers, line.split('\t')))
        if not row.get('ID', '').isdigit() or row.get('Type') not in ('0', '1'):
            continue
        kind, pool_id = int(row['Type']), int(row['ID'])
        sequence[kind, pool_id] += 1
        result.append(dict(type=kind, id=pool_id, segment=sequence[kind, pool_id],
                           name=lang.get(row.get('Name'), row.get('Comment', '')),
                           pool=row['Pool'], original=row.get('Time', ''),
                           one=row.get('CostOne', ''), ten=row.get('CostTen', ''),
                           judge=row.get('JudgeType', ''), protect=row.get('ProtectNum', ''),
                           up=row.get('ProtectUpNum', ''), rate=row.get('ExchangeRate', '')))
    return result


def validate(plan, pools):
    if type(plan.get('enabled')) is not bool or not isinstance(plan.get('entries'), list):
        raise ValueError('设置格式不正确')
    if len(plan['entries']) > 1000:
        raise ValueError('最多保存1000条排期')
    known = {(r['type'], r['id'], r['segment']) for r in pools}
    groups = defaultdict(list)
    for entry in plan['entries']:
        if not isinstance(entry, dict) or any(type(entry.get(k)) is not int for k in ('type', 'id', 'segment', 'start', 'end')):
            raise ValueError('排期字段不完整')
        if (entry['type'], entry['id'], entry['segment']) not in known:
            raise ValueError('排期包含不存在的卡池或档期')
        if not 0 < entry['start'] < entry['end'] <= 253402300799:
            raise ValueError('结束时间必须晚于开始时间')
        groups[entry['type'], entry['id']].append(entry)
    for group in groups.values():
        end = 0
        for entry in sorted(group, key=lambda e: e['start']):
            if entry['start'] < end:
                raise ValueError('同类型、同ID的排期不能重叠；可首尾相接')
            end = entry['end']


def load(pools):
    plan = json.loads(PLAN.read_text(encoding='utf-8-sig')) if PLAN.exists() else dict(enabled=False, entries=[])
    validate(plan, pools)
    return plan


def save(plan, pools):
    validate(plan, pools)
    backup = None
    if PLAN.exists():
        backups = ROOT / 'data' / 'gacha-rotation-backups'
        backups.mkdir(parents=True, exist_ok=True)
        backup = backups / (datetime.now(ZONE).strftime('%Y%m%d-%H%M%S-%f') + '.json')
        shutil.copy2(PLAN, backup)
    handle, name = tempfile.mkstemp(prefix='gacha-rotation-', suffix='.tmp', dir=SETTINGS)
    try:
        with os.fdopen(handle, 'w', encoding='utf-8') as stream:
            json.dump(plan, stream, ensure_ascii=False, indent=2)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(name, PLAN)
    finally:
        if os.path.exists(name):
            os.unlink(name)
    return backup


def display_time(seconds):
    return datetime.fromtimestamp(seconds, ZONE).strftime(FORMAT)


def main():
    import tkinter as tk
    from tkinter import ttk, messagebox
    pools = catalog()
    window = tk.Tk()
    window.title('双生视界 · 卡池轮换管理')
    window.geometry('1180x780')
    window.minsize(900, 650)
    try:
        plan = load(pools)
    except (ValueError, OSError) as error:
        messagebox.showerror('无法读取现有排期', str(error) + '\n请先修复或从备份恢复文件。')
        window.destroy()
        return
    entries = plan['entries']
    enabled = tk.BooleanVar(value=plan['enabled'])
    search = tk.StringVar()
    start = tk.StringVar(value=datetime.now(ZONE).strftime(FORMAT))
    finish = tk.StringVar(value=(datetime.now(ZONE) + timedelta(days=7)).strftime(FORMAT))
    note = tk.StringVar(value='未保存的编辑不会影响游戏。所有输入时间均为北京时间。')
    baseline = PLAN.read_text(encoding='utf-8-sig') if PLAN.exists() else None
    frame = ttk.Frame(window, padding=14)
    frame.pack(fill='both', expand=True)
    ttk.Label(frame, text='卡池轮换管理', font=('Microsoft YaHei UI', 19, 'bold')).pack(anchor='w')
    ttk.Label(frame, text='选择历史卡组 → 设置开放时间 → 加入排期 → 保存生效。原版卡池、概率、价格与玩家保底进度保持原值。').pack(anchor='w', pady=(4, 8))
    ttk.Checkbutton(frame, text='接管全部角色 / 武器卡池轮换（未排期的卡池关闭）', variable=enabled).pack(anchor='w')
    ttk.Label(frame, text='关闭接管并保存，可恢复全部原版日期。相同ID不同档期共用该ID原有保底 / 自选任务；排期不会重置进度。').pack(anchor='w', pady=5)
    find = ttk.Frame(frame)
    find.pack(fill='x', pady=5)
    ttk.Label(find, text='搜索名称 / ID / 卡组：').pack(side='left')
    ttk.Entry(find, textvariable=search).pack(side='left', fill='x', expand=True)

    def tree(parent, columns, height):
        box = ttk.Frame(parent)
        box.pack(fill='both', expand=True, pady=4)
        widget = ttk.Treeview(box, columns=[c[0] for c in columns], show='headings', height=height, selectmode='browse')
        scroll = ttk.Scrollbar(box, orient='vertical', command=widget.yview)
        widget.configure(yscrollcommand=scroll.set)
        for key, title, width in columns:
            widget.heading(key, text=title)
            widget.column(key, width=width, minwidth=50)
        widget.pack(side='left', fill='both', expand=True)
        scroll.pack(side='right', fill='y')
        return widget

    available = tree(frame, [('type', '类型', 65), ('id', 'ID', 55), ('segment', '档期序号', 65), ('name', '名称', 170), ('pool', '卡组', 290), ('original', '原开放时间', 250)], 8)
    detail = tk.StringVar(value='选中一行可查看该档期的消耗与保底配置。')
    ttk.Label(frame, textvariable=detail, wraplength=1100).pack(anchor='w')

    def refresh_catalog(*_):
        available.delete(*available.get_children())
        text = search.get().strip().lower()
        for idx, row in enumerate(pools):
            if text and text not in f'{row["id"]} {row["name"]} {row["pool"]}'.lower():
                continue
            available.insert('', 'end', iid=str(idx), values=('角色' if row['type'] else '武器', row['id'], row['segment'], row['name'], row['pool'], row['original']))

    def selected(_=None):
        if available.selection():
            row = pools[int(available.selection()[0])]
            detail.set(f'单抽：{row["one"] or "无"}　十连：{row["ten"] or "无"}　地球币单价：{row["rate"] or "无"}　普通保底：{row["protect"] or "无"}　UP保底：{row["up"] or "无"}　判断类型：{row["judge"]}。原版门票 / 通关要求仍然生效。')

    available.bind('<<TreeviewSelect>>', selected)
    search.trace_add('write', refresh_catalog)
    refresh_catalog()
    edit = ttk.Frame(frame)
    edit.pack(fill='x', pady=7)
    ttk.Label(edit, text='开始（北京时间）：').pack(side='left')
    ttk.Entry(edit, textvariable=start, width=19).pack(side='left')
    ttk.Label(edit, text='  结束：').pack(side='left')
    ttk.Entry(edit, textvariable=finish, width=19).pack(side='left')
    schedule = tree(frame, [('type', '类型', 55), ('id', 'ID', 50), ('name', '名称 / 档期', 210), ('start', '开始（北京）', 175), ('end', '结束（北京）', 175), ('status', '状态', 80)], 7)
    lookup = {(r['type'], r['id'], r['segment']): r for r in pools}

    def refresh_schedule():
        schedule.delete(*schedule.get_children())
        now = datetime.now(ZONE).timestamp()
        for idx, entry in enumerate(entries):
            row = lookup[entry['type'], entry['id'], entry['segment']]
            state = '开放中' if entry['start'] <= now < entry['end'] else '未开始' if now < entry['start'] else '已结束'
            schedule.insert('', 'end', iid=str(idx), values=('角色' if entry['type'] else '武器', entry['id'], f'{row["name"]} / 档期{entry["segment"]}', display_time(entry['start']), display_time(entry['end']), state if enabled.get() else '未接管'))

    def add():
        if not available.selection():
            messagebox.showinfo('请选择卡池', '先在上方列表选择一个卡池档期。')
            return
        try:
            row = pools[int(available.selection()[0])]
            if not (SETTINGS / (row['pool'] + '.txt')).is_file():
                raise ValueError('该卡组文件缺失，无法加入排期')
            entry = {k: row[k] for k in ('type', 'id', 'segment')}
            entry.update(start=int(datetime.strptime(start.get().strip(), FORMAT).replace(tzinfo=ZONE).timestamp()), end=int(datetime.strptime(finish.get().strip(), FORMAT).replace(tzinfo=ZONE).timestamp()))
            validate(dict(enabled=True, entries=entries + [entry]), pools)
            entries.append(entry)
            refresh_schedule()
            note.set('已加入排期，点击“保存生效”后生效。时间格式：2026-10-24 12:00')
        except ValueError as error:
            messagebox.showerror('不能加入排期', str(error) + '\n时间格式：年-月-日 时:分')

    def remove():
        if schedule.selection():
            del entries[int(schedule.selection()[0])]
            refresh_schedule()
            note.set('已移除，保存后生效。')

    def persist():
        nonlocal baseline
        try:
            current = PLAN.read_text(encoding='utf-8-sig') if PLAN.exists() else None
            if current != baseline:
                raise ValueError('排期已被其他窗口修改，请关闭并重新打开本程序，以免覆盖他人的修改。')
            backup = save(dict(enabled=enabled.get(), entries=entries), pools)
            baseline = PLAN.read_text(encoding='utf-8-sig')
            refresh_schedule()
            note.set('已保存。运行中的新版服务端约1秒内读取；退出补给页后重新进入刷新。' + (' 已备份上一次设置。' if backup else ''))
        except (ValueError, OSError) as error:
            messagebox.showerror('保存失败', str(error))

    def restore():
        enabled.set(False)
        persist()

    ttk.Button(edit, text='加入排期', command=add).pack(side='left', padx=14)
    actions = ttk.Frame(frame)
    actions.pack(fill='x', pady=6)
    ttk.Button(actions, text='移除所选排期', command=remove).pack(side='left')
    ttk.Button(actions, text='恢复原版日期', command=restore).pack(side='left', padx=8)
    ttk.Button(actions, text='保存生效', command=persist).pack(side='right')
    ttk.Label(frame, textvariable=note, wraplength=1100).pack(anchor='w', pady=5)
    enabled.trace_add('write', lambda *_: refresh_schedule())
    refresh_schedule()

    def close():
        current = dict(enabled=enabled.get(), entries=entries)
        original = json.loads(baseline) if baseline else dict(enabled=False, entries=[])
        if current != original and not messagebox.askyesno('有未保存修改', '关闭并放弃未保存的修改？'):
            return
        window.destroy()

    window.protocol('WM_DELETE_WINDOW', close)
    window.mainloop()


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--check', action='store_true', help='Validate catalog and schedule without opening a window')
    parser.add_argument('--setting-dir', type=Path, help='Directory containing gacha.txt and pool files')
    parser.add_argument('--lang-file', type=Path, help='Optional Chinese localization JSON')
    args = parser.parse_args()
    if args.setting_dir:
        SETTINGS = args.setting_dir.resolve()
        PLAN = SETTINGS / 'gacha-rotation.json'
    if args.lang_file:
        LANGUAGE = args.lang_file.resolve()
    if args.check:
        rows = catalog()
        plan = load(rows)
        print(json.dumps(dict(catalog=len(rows), enabled=plan['enabled'], entries=len(plan['entries'])), ensure_ascii=False))
    else:
        main()
