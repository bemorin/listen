#![windows_subsystem = "windows"]
slint::slint! {
    export component Hello inherits Window {
        width: 500px; height: 410px;
        no-frame: true;
        background: #171717;
        title: "Listen hello";
        Rectangle {
            width: 116px; height: 116px; border-radius: 20px;
            background: #202020; border-color: #303030; border-width: 1px;
            Text { text: "♪"; font-size: 54px; color: #D4D4D4; }
        }
        Text { y: 300px; text: "Back Up"; font-size: 22px; font-weight: 600; color: #F1F1F1; }
        TouchArea { clicked => { } }
    }
}
fn main() { Hello::new().unwrap().run().unwrap(); }
