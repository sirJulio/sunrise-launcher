
var state_enum = {
    unchecked: 0,
    ready: 1,
    updating: 2,
    error: 3
};

function init() {
    button_refresh.enabled = false;
    button_verify.enabled = false;
    button_start.enabled = false;
    serverlist.update.connect(refreshAll);
    serverlist.message.connect(showMessage);
    serverlist.progress.connect(refreshProgress);
    var task = serverlist.loadAsync("./servers.json");
    Net.await(task, function () {
        button_refresh.enabled = true;
        if (serverlist.servers.count == 0) {
            configNew();
        }
    });
}

function refreshAll() {
    refreshList();
    refreshSelected();
}

function select(manifestURL) {
    serverlist.selected = manifestURL;
    refreshSelected();
}

function refreshList() {
    repeaterServers.model = Net.toListModel(serverlist.servers);
}

function refreshSelected() {
    var server = serverlist.get(serverlist.selected);

    if (server == null || server.state !== state_enum.ready) {
        button_start.enabled = false;
        button_verify.enabled = false;
    } else if (server.state === state_enum.ready) {
        button_start.enabled = true;
        button_verify.enabled = true;
    }

    if (server != null) {
        refreshProgress(server.manifestURL, server.taskName, server.taskDone, server.taskCount)
    } else {
        refreshProgress(null, null, 0, 0)
    }
    
}

function inspect(obj) {
    for (var name in obj) {
        console.log(name);
    }
}

function verifyServer() {
    button_verify.enabled = false;
    button_start.enabled = false;
    var task = serverlist.verifyAsync();
    Net.await(task, function () {
        refreshSelected();
    });
}

function refreshServers() {
    button_refresh.enabled = false;
    button_verify.enabled = false;
    button_start.enabled = false;
    var task = serverlist.refreshAsync();
    Net.await(task, function () {
        button_refresh.enabled = true;
    });
    refreshSelected();
}

function startServer() {
    button_verify.enabled = false;
    button_start.enabled = false;
    applicationWindow.lower();
    serverlist.launch();
    refreshSelected();
}

function removeOpen(manifestURL) {
    var server = serverlist.get(manifestURL);
    if (server == null) return;

    dialog_remove.manifestURL = manifestURL;
    dialog_remove.text = "Are you sure you wish to remove server '" + server.title + "'?";
    dialog_remove.visible = true;
}

function removeServer() {
    serverlist.remove(dialog_remove.manifestURL);
    refresh();
}

function configOpen(manifestURL) {
    var server = serverlist.get(manifestURL);
    if (server == null) return;

    textfield_manifesturl.text = server.manifestURL;
    textfield_installpath.text = server.installPath;

    populateLaunchOptions(server.metadata);
    combobox_launch.currentIndex = combobox_launch.find(server.launch);

    dialog_config.manifestURL = manifestURL;
    dialog_config.visible = true;
}

function populateLaunchOptions(metadata) {
    var options = [];
    Net.listForEach(metadata.launchOptions, function (option) {
        options.push(option.name);
    });
    combobox_launch.model = options;
}

function configUrlChanged() {
    combobox_launch.currentIndex = -1;
    combobox_launch.model = [];
}

function findUrl() {
    combobox_launch.currentIndex = -1;
    combobox_launch.model = [];

    if (textfield_manifesturl.text === "") {
        showInfo("Please enter a URL for the manifest.")
        return;
    }

    var task = serverlist.findMetadataAsync(textfield_manifesturl.text);
    Net.await(task, function (metadata) {
        if (metadata == null) {
            showMessage("Could not retrieve manifest at that location.")
            return;
        }
        populateLaunchOptions(metadata);
        combobox_launch.currentIndex = 0;
    });
}

function configNew() {
    textfield_manifesturl.text = "";
    textfield_installpath.text = "";
    dialog_config.manifestURL = "";
    combobox_launch.currentIndex = -1;
    combobox_launch.model = [];
    dialog_config.title = "New Server";
    dialog_config.visible = true;
    
}

function configSave(action) {
    if (action.button == StandardButton.Cancel)
        return;

    if (textfield_manifesturl.text === "") {
        showInfo("Please enter a URL for the manifest.")
        action.accepted = false;
        return;
    }

    //auto "find" and select first launch option
    if (combobox_launch.currentIndex == -1) {
        var task = serverlist.findMetadataAsync(textfield_manifesturl.text);
        Net.await(task, function (metadata) {
            if (metadata == null) {
                showMessage("Could not retrieve manifest at that location.")
                return;
            }
            populateLaunchOptions(metadata);
            combobox_launch.currentIndex = 0;

            if (dialog_config.manifestURL === "") {
                serverlist.addAsync(textfield_manifesturl.text, textfield_installpath.text, combobox_launch.currentText);
                return;
            }
            serverlist.configAsync(dialog_config.manifestURL, textfield_manifesturl.text, textfield_installpath.text, combobox_launch.currentText);
        });
        return;
    }

    if (dialog_config.manifestURL === "") {
        serverlist.addAsync(textfield_manifesturl.text, textfield_installpath.text, combobox_launch.currentText);
        return;
    }
    serverlist.configAsync(dialog_config.manifestURL, textfield_manifesturl.text, textfield_installpath.text, combobox_launch.currentText);
}

function showError(error) {
    if (error == null) return;
    dialogError.text = error;
    dialogError.visible = true;
}

function showMessage(msg) {
    dialogError.text = msg;
    dialogError.visible = true;
}

function showInfo(msg) {
    dialogInfo.text = msg;
    dialogInfo.visible = true;
}

function refreshProgress(manifestURL, taskName, taskDone, taskCount) {
    if (serverlist.selected == null || taskName == "") {
        paneProgress.visible = false;
        return;
    }

    if (serverlist.selected != manifestURL)
        return;

    if (taskCount == 0) {
        progressBar.indeterminate = true;
    } else {
        progressBar.indeterminate = false;
        progressBar.value = taskDone;
        progressBar.to = taskCount;
    }

    textProgress.text = taskName;
    paneProgress.visible = true;
}