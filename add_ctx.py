path = r'D:\MemoryTool\src\MemoryTool.App\MainWindow.xaml'
with open(path, 'r', encoding='utf-8') as fh:
    t = fh.read()
old = '                    <DataGrid Grid.Row="1" x:Name="ThreadGrid" ItemsSource="{Binding Threads}" AutoGenerateColumns="False" IsReadOnly="True">'
new = '''                    <DataGrid Grid.Row="1" x:Name="ThreadGrid" ItemsSource="{Binding Threads}" AutoGenerateColumns="False" IsReadOnly="True">
                        <DataGrid.ContextMenu>
                            <ContextMenu>
                                <MenuItem Header="挂起线程" Click="CtxSuspendThread_Click"/>
                                <MenuItem Header="恢复线程" Click="CtxResumeThread_Click"/>
                            </ContextMenu>
                        </DataGrid.ContextMenu>'''
assert old in t, 'old not found'
t = t.replace(old, new)
with open(path, 'w', encoding='utf-8') as fh:
    fh.write(t)
print('done')