import gi
gi.require_version("Gtk","4.0")
gi.require_version("Adw","1")
from gi.repository import Adw

class MainWindow(Adw.ApplicationWindow):
    def __init__(self, application, bridge):
        super().__init__(application=application)
        raise NotImplementedError
